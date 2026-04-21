using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Converters;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Persistence
{
    /// <summary>
    /// 使用 JSON 文件持久化文档级 Todo Board。
    /// </summary>
    public sealed class JsonTodoStore : ITodoStore
    {
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new StringEnumConverter() }
        };

        private readonly string _rootDirectory;

        public JsonTodoStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartWord",
                "todo"))
        {
        }

        public JsonTodoStore(string rootDirectory)
        {
            _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartWord", "todo")
                : rootDirectory;
        }

        public Task<TodoBoard> GetBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var boardPath = ResolveBoardPath(documentPath);
            if (!File.Exists(boardPath))
            {
                return Task.FromResult<TodoBoard>(null);
            }

            var json = File.ReadAllText(boardPath, Encoding.UTF8);
            var board = JsonConvert.DeserializeObject<TodoBoard>(json);
            if (board == null)
            {
                throw new InvalidOperationException("Todo Board 文件内容为空或无法反序列化。");
            }

            return Task.FromResult(board);
        }

        public Task SaveBoardAsync(TodoBoard board, CancellationToken cancellationToken)
        {
            if (board == null)
            {
                throw new ArgumentNullException(nameof(board));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_rootDirectory);
            var boardPath = ResolveBoardPath(board.DocumentPath);
            var json = JsonConvert.SerializeObject(board, SerializerSettings);
            File.WriteAllText(boardPath, json, Encoding.UTF8);
            return Task.CompletedTask;
        }

        public Task DeleteBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var boardPath = ResolveBoardPath(documentPath);
            if (File.Exists(boardPath))
            {
                File.Delete(boardPath);
            }

            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string documentPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(File.Exists(ResolveBoardPath(documentPath)));
        }

        private string ResolveBoardPath(string documentPath)
        {
            var normalized = string.IsNullOrWhiteSpace(documentPath)
                ? "__active_document__"
                : documentPath.Trim();
            var hash = ComputeStableHash(normalized);
            return Path.Combine(_rootDirectory, hash + ".json");
        }

        private static string ComputeStableHash(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var item in hash)
                {
                    builder.Append(item.ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
