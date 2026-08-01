using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Skills
{
    /// <summary>
    /// 处理外部 Skill 的下载、预览和原子安装。该类绝不执行包内脚本。
    /// </summary>
    public sealed class FileSystemSkillPackageInstaller : ISkillPackageInstaller
    {
        private const int MaxDownloadBytes = 20 * 1024 * 1024;
        private const long MaxExpandedBytes = 100L * 1024 * 1024;
        private const long MaxSingleFileBytes = 20L * 1024 * 1024;
        private const int MaxFileCount = 1000;
        private const int MaxPathDepth = 16;
        private const int MaxMarkdownBytes = 64 * 1024;
        private const int MaxRedirects = 5;
        private const int NetworkTimeoutSeconds = 30;
        private const int SessionTtlMinutes = 30;
        private const int CleanupTtlHours = 24;
        private const string SkillFileName = "SKILL.md";
        private const string MarkerFileName = ".smartword-source.json";

        private readonly string _builtInRoot;
        private readonly string _userRoot;
        private readonly string _importRoot;
        private readonly HttpClient _httpClient;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, PreviewSession> _sessions =
            new Dictionary<string, PreviewSession>(StringComparer.OrdinalIgnoreCase);

        public FileSystemSkillPackageInstaller()
            : this(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Skills"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SmartWord",
                    "skills"))
        {
        }

        public FileSystemSkillPackageInstaller(string builtInRoot, string userRoot)
        {
            _builtInRoot = Path.GetFullPath(builtInRoot ?? string.Empty);
            _userRoot = Path.GetFullPath(userRoot ?? string.Empty);
            _importRoot = Path.Combine(_userRoot, ".imports");
            Directory.CreateDirectory(_importRoot);
            CleanupExpiredImportDirectories();

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(NetworkTimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SmartWord-SkillImporter/1.0");
        }

        public async Task<SkillImportPreview> PreviewNetworkAsync(
            string sourceUrl,
            CancellationToken cancellationToken)
        {
            var normalizedUrl = NormalizeSourceUrl(sourceUrl);
            var session = CreateSession();
            var item = new SkillImportPreviewItem
            {
                ItemId = Guid.NewGuid().ToString("N"),
                SourceKind = "network",
                Source = normalizedUrl
            };
            var itemRoot = Path.Combine(session.RootPath, item.ItemId);
            Directory.CreateDirectory(itemRoot);

            try
            {
                var zipPath = Path.Combine(itemRoot, "package.zip");
                await DownloadPackageAsync(normalizedUrl, zipPath, cancellationToken).ConfigureAwait(false);
                var extractedRoot = Path.Combine(itemRoot, "extracted");
                Directory.CreateDirectory(extractedRoot);
                ExtractZipSafely(zipPath, extractedRoot, cancellationToken);
                var skillRoot = FindZipSkillRoot(extractedRoot);
                var prepared = ValidateSkillRoot(skillRoot, item.Source, "network", cancellationToken);
                ApplyPreview(item, prepared);
                session.Items[item.ItemId] = new PreparedItem(item, skillRoot, true, string.Empty);
            }
            catch (Exception ex)
            {
                item.Errors = new[] { ex.Message };
                item.CanInstall = false;
                session.Items[item.ItemId] = new PreparedItem(item, string.Empty, true, string.Empty);
            }

            return ToPreview(session);
        }

        public Task<SkillImportPreview> PreviewFoldersAsync(
            IReadOnlyList<string> folderPaths,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var session = CreateSession();
                var uniquePaths = (folderPaths ?? new List<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => Path.GetFullPath(path.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var sourcePath in uniquePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = new SkillImportPreviewItem
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        SourceKind = "folder",
                        Source = sourcePath
                    };
                    try
                    {
                        var prepared = ValidateSkillRoot(sourcePath, sourcePath, "folder", cancellationToken);
                        ApplyPreview(item, prepared);
                        session.Items[item.ItemId] = new PreparedItem(item, sourcePath, false, sourcePath);
                    }
                    catch (Exception ex)
                    {
                        item.Errors = new[] { ex.Message };
                        item.CanInstall = false;
                        session.Items[item.ItemId] = new PreparedItem(item, string.Empty, false, sourcePath);
                    }
                }

                return ToPreview(session);
            }, cancellationToken);
        }

        public Task<SkillImportResult> InstallAsync(
            SkillImportInstallRequest request,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => InstallInternal(request, cancellationToken), cancellationToken);
        }

        public Task CancelPreviewAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RemoveSession(sessionId);
            }, cancellationToken);
        }

        /// <summary>
        /// 供基础设施测试验证 ZIP 路径防护；正式流程只通过预览入口调用。
        /// </summary>
        internal static void ExtractZipForTests(
            string zipPath,
            string destinationRoot,
            CancellationToken cancellationToken)
        {
            ExtractZipSafely(zipPath, destinationRoot, cancellationToken);
        }

        private SkillImportResult InstallInternal(
            SkillImportInstallRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
            {
                throw new ArgumentException("导入预览会话不能为空。", nameof(request));
            }

            PreviewSession session;
            lock (_syncRoot)
            {
                CleanupExpiredImportDirectories();
                if (!_sessions.TryGetValue(request.SessionId, out session)
                    || session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    throw new InvalidOperationException("导入预览已过期，请重新预览。");
                }
            }

            var requestedIds = (request.ItemIds ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var results = new List<SkillImportResultItem>();
            foreach (var itemId in requestedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!session.Items.TryGetValue(itemId, out var prepared))
                {
                    results.Add(new SkillImportResultItem
                    {
                        ItemId = itemId,
                        Success = false,
                        Message = "未找到导入预览项。"
                    });
                    continue;
                }

                results.Add(InstallItem(session, prepared, cancellationToken));
            }

            RemoveSession(request.SessionId);
            return new SkillImportResult { Items = results };
        }

        private SkillImportResultItem InstallItem(
            PreviewSession session,
            PreparedItem prepared,
            CancellationToken cancellationToken)
        {
            var result = new SkillImportResultItem
            {
                ItemId = prepared.Preview.ItemId,
                Name = prepared.Preview.Name
            };
            if (!prepared.Preview.CanInstall)
            {
                result.Message = prepared.Preview.Errors.FirstOrDefault() ?? "导入项校验失败。";
                return result;
            }

            string stagingPath = null;
            try
            {
                var current = ValidateSkillRoot(
                    prepared.SourceKind == "folder" ? prepared.SourcePath : prepared.RootPath,
                    prepared.Preview.Source,
                    prepared.SourceKind,
                    cancellationToken);
                if (!string.Equals(current.ContentSha256, prepared.Preview.ContentSha256, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(current.Name, prepared.Preview.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("源内容在预览后发生变化，请重新预览。");
                }

                var name = SkillPathGuard.NormalizeSkillName(current.Name);
                SkillPathGuard.EnsureValidSkillName(name);
                if (Directory.Exists(SkillPathGuard.CombineSkillRoot(_builtInRoot, name)))
                {
                    throw new InvalidOperationException("不能覆盖或冒充内置 Skill。");
                }

                var targetPath = SkillPathGuard.CombineSkillRoot(_userRoot, name);
                if (Directory.Exists(targetPath))
                {
                    throw new InvalidOperationException("同名 Skill 已存在，安装已拒绝。");
                }

                Directory.CreateDirectory(_userRoot);
                stagingPath = Path.Combine(_userRoot, ".installing-" + Guid.NewGuid().ToString("N"));
                SkillPathGuard.EnsureInsideRoot(_userRoot, stagingPath);
                Directory.CreateDirectory(stagingPath);
                CopyPackageTree(
                    prepared.SourceKind == "folder" ? prepared.SourcePath : prepared.RootPath,
                    stagingPath,
                    cancellationToken);
                WriteExternalMarker(stagingPath, prepared.Preview.Source, prepared.Preview.ContentSha256);

                var stagedValidation = ValidateSkillRoot(stagingPath, prepared.Preview.Source, "folder", cancellationToken);
                if (!string.Equals(stagedValidation.ContentSha256, prepared.Preview.ContentSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("安装暂存内容校验失败。");
                }

                Directory.Move(stagingPath, targetPath);
                stagingPath = null;
                result.Success = true;
                result.Message = "Skill 已安装，外部脚本保持禁用。";
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
            }
            finally
            {
                DeleteDirectoryIfExists(stagingPath);
            }

            return result;
        }

        private void ApplyPreview(SkillImportPreviewItem item, ValidatedSkill prepared)
        {
            item.Name = prepared.Name;
            item.DisplayName = prepared.DisplayName;
            item.Description = prepared.Description;
            item.Version = prepared.Version;
            item.ContentSha256 = prepared.ContentSha256;
            item.TotalBytes = prepared.TotalBytes;
            item.FileCount = prepared.FileCount;
            item.ResourceCount = prepared.ResourceCount;
            item.ScriptCount = prepared.ScriptCount;
            item.RequiredTools = prepared.RequiredTools;
            item.Warnings = new[] { "来源为外部 Skill，脚本默认禁用；安装不会执行包内代码。" };
            var errors = new List<string>();
            if (Directory.Exists(SkillPathGuard.CombineSkillRoot(_builtInRoot, prepared.Name)))
            {
                errors.Add("同名内置 Skill 已存在，不能覆盖或冒充内置 Skill。");
            }
            else if (Directory.Exists(SkillPathGuard.CombineSkillRoot(_userRoot, prepared.Name)))
            {
                errors.Add("同名 Skill 已存在，第一版不会覆盖现有内容。");
            }

            item.Errors = errors;
            item.CanInstall = errors.Count == 0;
        }

        private ValidatedSkill ValidateSkillRoot(
            string rootPath,
            string source,
            string sourceKind,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                throw new DirectoryNotFoundException("未找到指定 Skill 文件夹。");
            }

            var root = Path.GetFullPath(rootPath);
            EnsureNoReparsePoint(root, root);
            var skillFilePath = Path.Combine(root, SkillFileName);
            if (!File.Exists(skillFilePath))
            {
                throw new InvalidOperationException("Skill 根目录必须直接包含 SKILL.md。");
            }

            EnsureNoReparsePoint(root, skillFilePath);
            var markdownInfo = new FileInfo(skillFilePath);
            if (markdownInfo.Length > MaxMarkdownBytes)
            {
                throw new InvalidOperationException("SKILL.md 超过 64KB，已拒绝导入。");
            }

            var content = File.ReadAllText(skillFilePath, Encoding.UTF8);
            var declaredName = SkillFrontmatterParser.ReadFrontmatterName(content);
            if (!SkillPathGuard.IsValidSkillName(declaredName))
            {
                throw new InvalidOperationException("SKILL.md 必须声明合法的 name。");
            }

            var definition = SkillFrontmatterParser.Parse(content, declaredName);
            var files = ScanFiles(root, cancellationToken);
            var packageHash = ComputePackageHash(root, files, cancellationToken);
            var resourceCount = files.Count(file => IsResourcePath(file.RelativePath));
            var scriptCount = files.Count(file => IsScriptPath(file.RelativePath));
            return new ValidatedSkill
            {
                Name = declaredName,
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                Version = definition.Version,
                RequiredTools = definition.RequiredTools,
                ContentSha256 = packageHash,
                TotalBytes = files.Sum(file => file.SizeBytes),
                FileCount = files.Count,
                ResourceCount = resourceCount,
                ScriptCount = scriptCount
            };
        }

        private List<PackageFile> ScanFiles(string root, CancellationToken cancellationToken)
        {
            var files = new List<PackageFile>();
            long totalBytes = 0;
            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(Path.GetFullPath(root));
            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentDirectory = pendingDirectories.Pop();
                EnsureNoReparsePoint(root, currentDirectory);
                foreach (var directory in Directory.GetDirectories(currentDirectory))
                {
                    EnsureNoReparsePoint(root, directory);
                    pendingDirectories.Push(directory);
                }

                foreach (var path in Directory.GetFiles(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureNoReparsePoint(root, path);
                    var relativePath = path.Substring(root.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (string.Equals(relativePath, MarkerFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ValidateRelativePath(relativePath);
                    if (!relativePaths.Add(relativePath))
                    {
                        throw new InvalidOperationException("Skill 包含重复文件路径。");
                    }

                    var info = new FileInfo(path);
                    if (info.Length > MaxSingleFileBytes)
                    {
                        throw new InvalidOperationException("Skill 单文件超过 20MB，已拒绝导入。");
                    }

                    totalBytes += info.Length;
                    if (totalBytes > MaxExpandedBytes)
                    {
                        throw new InvalidOperationException("Skill 解压后总容量超过 100MB，已拒绝导入。");
                    }

                    files.Add(new PackageFile { AbsolutePath = path, RelativePath = relativePath, SizeBytes = info.Length });
                    if (files.Count > MaxFileCount)
                    {
                        throw new InvalidOperationException("Skill 文件数量超过 1000 个，已拒绝导入。");
                    }
                }
            }

            return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ComputePackageHash(
            string root,
            IReadOnlyList<PackageFile> files,
            CancellationToken cancellationToken)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pathBytes = Encoding.UTF8.GetBytes(file.RelativePath.ToLowerInvariant());
                    sha.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);
                    var separator = new byte[] { 0 };
                    sha.TransformBlock(separator, 0, separator.Length, separator, 0);
                    using (var stream = File.OpenRead(file.AbsolutePath))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sha.TransformBlock(buffer, 0, read, buffer, 0);
                        }
                    }
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private void CopyPackageTree(
            string sourceRoot,
            string targetRoot,
            CancellationToken cancellationToken)
        {
            foreach (var file in ScanFiles(sourceRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = file.AbsolutePath;
                var relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                if (string.Equals(relativePath, MarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
                SkillPathGuard.EnsureInsideRoot(targetRoot, targetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, false);
            }
        }

        private void WriteExternalMarker(string targetRoot, string source, string packageHash)
        {
            var marker = new ExternalSourceMetadata
            {
                TrustLevel = "external",
                ScriptPolicy = "disabled",
                Source = source,
                PackageSha256 = packageHash,
                ImportedAtUtc = DateTimeOffset.UtcNow
            };
            File.WriteAllText(
                Path.Combine(targetRoot, MarkerFileName),
                JsonConvert.SerializeObject(marker, Formatting.Indented),
                new UTF8Encoding(false));
        }

        private async Task DownloadPackageAsync(
            string sourceUrl,
            string targetPath,
            CancellationToken cancellationToken)
        {
            var candidates = await ResolveDownloadUrlsAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
            Exception lastError = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    await DownloadUrlAsync(candidate, targetPath, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                }
            }

            throw lastError ?? new InvalidOperationException("无法下载 Skill 包。");
        }

        private async Task<IReadOnlyList<Uri>> ResolveDownloadUrlsAsync(
            string sourceUrl,
            CancellationToken cancellationToken)
        {
            var source = new Uri(sourceUrl, UriKind.Absolute);
            if (!IsGitHubRepositoryUrl(source))
            {
                return new[] { source };
            }

            var segments = source.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var apiUri = new Uri("https://api.github.com/repos/" + segments[0] + "/" + segments[1]);
            await EnsurePublicHttpsAsync(apiUri, cancellationToken).ConfigureAwait(false);
            using (var response = await _httpClient.GetAsync(apiUri, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException("GitHub 仓库不存在或不可公开访问。");
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var defaultBranch = JObject.Parse(json).Value<string>("default_branch");
                if (string.IsNullOrWhiteSpace(defaultBranch))
                {
                    throw new InvalidOperationException("无法读取 GitHub 仓库默认分支。");
                }

                var zipUri = new Uri(
                    "https://github.com/" + segments[0] + "/" + segments[1]
                    + "/archive/refs/heads/" + Uri.EscapeDataString(defaultBranch) + ".zip");
                return new[] { zipUri };
            }
        }

        private async Task DownloadUrlAsync(Uri initialUri, string targetPath, CancellationToken cancellationToken)
        {
            var current = initialUri;
            for (var redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                await EnsurePublicHttpsAsync(current, cancellationToken).ConfigureAwait(false);
                using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                using (var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        if (redirect == MaxRedirects || response.Headers.Location == null)
                        {
                            throw new HttpRequestException("Skill 下载重定向次数超过限制。");
                        }

                        current = response.Headers.Location.IsAbsoluteUri
                            ? response.Headers.Location
                            : new Uri(current, response.Headers.Location);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength.HasValue
                        && response.Content.Headers.ContentLength.Value > MaxDownloadBytes)
                    {
                        throw new InvalidOperationException("Skill 下载包超过 20MB 限制。");
                    }

                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaxDownloadBytes)
                            {
                                throw new InvalidOperationException("Skill 下载包超过 20MB 限制。");
                            }

                            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    return;
                }
            }
        }

        private static void ExtractZipSafely(
            string zipPath,
            string destinationRoot,
            CancellationToken cancellationToken)
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                if (archive.Entries.Count > MaxFileCount * 2)
                {
                    throw new InvalidOperationException("ZIP 条目数量过多，已拒绝导入。");
                }

                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long declaredExpandedBytes = 0;
                long actualExpandedBytes = 0;
                var fileCount = 0;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = NormalizeZipPath(entry.FullName);
                    if (string.IsNullOrWhiteSpace(relativePath))
                    {
                        continue;
                    }

                    ValidateRelativePath(relativePath);
                    if (!paths.Add(relativePath))
                    {
                        throw new InvalidOperationException("ZIP 包含重复文件路径。");
                    }

                    var isDirectory = relativePath.EndsWith("/", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("/", StringComparison.Ordinal);
                    if (IsZipSymlink(entry))
                    {
                        throw new InvalidOperationException("ZIP 中包含符号链接，已拒绝导入。");
                    }

                    var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    SkillPathGuard.EnsureInsideRoot(destinationRoot, targetPath);
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(targetPath);
                        continue;
                    }

                    fileCount++;
                    if (fileCount > MaxFileCount || entry.Length > MaxSingleFileBytes)
                    {
                        throw new InvalidOperationException("ZIP 文件数量或单文件大小超过限制。");
                    }

                    declaredExpandedBytes += entry.Length;
                    if (declaredExpandedBytes > MaxExpandedBytes)
                    {
                        throw new InvalidOperationException("ZIP 解压后总容量超过 100MB，已拒绝导入。");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    using (var input = entry.Open())
                    using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long written = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            written += read;
                            actualExpandedBytes += read;
                            if (written > MaxSingleFileBytes || actualExpandedBytes > MaxExpandedBytes)
                            {
                                throw new InvalidOperationException("ZIP 实际解压大小超过限制。");
                            }

                            output.Write(buffer, 0, read);
                        }
                    }
                }
            }
        }

        private static string FindZipSkillRoot(string extractedRoot)
        {
            var direct = Path.Combine(extractedRoot, SkillFileName);
            if (File.Exists(direct))
            {
                return extractedRoot;
            }

            var directories = Directory.GetDirectories(extractedRoot);
            var candidates = directories
                .Where(directory => File.Exists(Path.Combine(directory, SkillFileName)))
                .ToList();
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            throw new InvalidOperationException("ZIP 必须直接包含 SKILL.md，或只有一层仓库包装目录。");
        }

        private static string NormalizeZipPath(string path)
        {
            // 保留前导斜杠，交给 ValidateRelativePath 拒绝 ZIP 中的绝对路径。
            return (path ?? string.Empty).Replace('\\', '/');
        }

        private static void ValidateRelativePath(string relativePath)
        {
            var normalized = (relativePath ?? string.Empty).Replace('\\', '/');
            if (Path.IsPathRooted(normalized)
                || normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Split('/').Any(part => part == "..")
                || normalized.Split('/').Length > MaxPathDepth)
            {
                throw new InvalidOperationException("Skill 包含越界或过深的文件路径。");
            }
        }

        private static bool IsZipSymlink(ZipArchiveEntry entry)
        {
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            return unixMode == 0xA000;
        }

        private static bool IsResourcePath(string relativePath)
        {
            return relativePath.StartsWith("references/", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsScriptPath(string relativePath)
        {
            return relativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)
                && (relativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                    || relativePath.EndsWith(".csx", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsGitHubRepositoryUrl(Uri uri)
        {
            var segments = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 2
                && !segments.Any(segment => segment.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeSourceUrl(string sourceUrl)
        {
            if (!Uri.TryCreate((sourceUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(uri.UserInfo)
                || (uri.Port != -1 && uri.Port != 443))
            {
                throw new InvalidOperationException("只支持不带凭据的 HTTPS Skill 地址。");
            }

            return uri.AbsoluteUri;
        }

        private static async Task EnsurePublicHttpsAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (uri == null
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(uri.UserInfo)
                || (uri.Port != -1 && uri.Port != 443))
            {
                throw new InvalidOperationException("下载重定向目标必须是 HTTPS 公网地址。");
            }

            IPAddress[] addresses;
            if (IPAddress.TryParse(uri.Host, out var literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (addresses.Length == 0 || addresses.Any(IsPrivateOrLocalAddress))
            {
                throw new InvalidOperationException("为避免访问本机或内网资源，Skill 下载地址必须解析到公网地址。");
            }
        }

        private static bool IsPrivateOrLocalAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254)
                    || bytes[0] == 0;
            }

            var ipv6 = address.GetAddressBytes();
            return (ipv6[0] & 0xFE) == 0xFC || (ipv6[0] == 0xFE && (ipv6[1] & 0xC0) == 0x80);
        }

        private PreviewSession CreateSession()
        {
            CleanupExpiredImportDirectories();
            var session = new PreviewSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                RootPath = Path.Combine(_importRoot, Guid.NewGuid().ToString("N")),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(SessionTtlMinutes)
            };
            Directory.CreateDirectory(session.RootPath);
            lock (_syncRoot)
            {
                _sessions[session.SessionId] = session;
            }

            return session;
        }

        private SkillImportPreview ToPreview(PreviewSession session)
        {
            return new SkillImportPreview
            {
                SessionId = session.SessionId,
                ExpiresAtUtc = session.ExpiresAtUtc,
                Items = session.Items.Values.Select(item => item.Preview).ToList()
            };
        }

        private void RemoveSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            PreviewSession session = null;
            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(sessionId, out session))
                {
                    _sessions.Remove(sessionId);
                }
            }

            DeleteDirectoryIfExists(session?.RootPath);
        }

        private void CleanupExpiredImportDirectories()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-CleanupTtlHours);
                if (Directory.Exists(_importRoot))
                {
                    foreach (var directory in Directory.GetDirectories(_importRoot))
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        {
                            DeleteDirectoryIfExists(directory);
                        }
                    }
                }

                // 安装过程中进程异常退出时，正式目录不会出现，但暂存目录可能遗留。
                if (Directory.Exists(_userRoot))
                {
                    foreach (var directory in Directory.GetDirectories(_userRoot, ".installing-*"))
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        {
                            DeleteDirectoryIfExists(directory);
                        }
                    }
                }

                lock (_syncRoot)
                {
                    foreach (var expired in _sessions.Values.Where(item => item.ExpiresAtUtc <= DateTimeOffset.UtcNow).ToList())
                    {
                        _sessions.Remove(expired.SessionId);
                        DeleteDirectoryIfExists(expired.RootPath);
                    }
                }
            }
            catch
            {
                // 临时目录清理失败不应阻止正常 Skill 预览；下次启动继续尝试。
            }
        }

        private static void EnsureNoReparsePoint(string root, string path)
        {
            var current = new FileInfo(path) as FileSystemInfo;
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Skill 路径包含符号链接、Junction 或其他 reparse point。");
                }

                if (string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
            }
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
                // 清理失败不能掩盖导入结果；TTL 清理会再次尝试。
            }
        }

        private sealed class PreviewSession
        {
            public string SessionId { get; set; }

            public string RootPath { get; set; }

            public DateTimeOffset ExpiresAtUtc { get; set; }

            public Dictionary<string, PreparedItem> Items { get; } =
                new Dictionary<string, PreparedItem>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PreparedItem
        {
            public PreparedItem(
                SkillImportPreviewItem preview,
                string rootPath,
                bool isTemporary,
                string sourcePath)
            {
                Preview = preview;
                RootPath = rootPath;
                IsTemporary = isTemporary;
                SourcePath = sourcePath;
                SourceKind = preview.SourceKind;
            }

            public SkillImportPreviewItem Preview { get; }

            public string RootPath { get; }

            public bool IsTemporary { get; }

            public string SourcePath { get; }

            public string SourceKind { get; }
        }

        private sealed class ValidatedSkill
        {
            public string Name { get; set; }

            public string DisplayName { get; set; }

            public string Description { get; set; }

            public string Version { get; set; }

            public string ContentSha256 { get; set; }

            public long TotalBytes { get; set; }

            public int FileCount { get; set; }

            public int ResourceCount { get; set; }

            public int ScriptCount { get; set; }

            public IReadOnlyList<string> RequiredTools { get; set; }
        }

        private sealed class PackageFile
        {
            public string AbsolutePath { get; set; }

            public string RelativePath { get; set; }

            public long SizeBytes { get; set; }
        }

        private sealed class ExternalSourceMetadata
        {
            [JsonProperty("trust_level")]
            public string TrustLevel { get; set; }

            [JsonProperty("script_policy")]
            public string ScriptPolicy { get; set; }

            [JsonProperty("source")]
            public string Source { get; set; }

            [JsonProperty("package_sha256")]
            public string PackageSha256 { get; set; }

            [JsonProperty("imported_at_utc")]
            public DateTimeOffset ImportedAtUtc { get; set; }
        }
    }
}
