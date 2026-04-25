using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Persistence
{
    /// <summary>
    /// 使用 JSON 文件持久化文档级 Todo Board。
    /// </summary>
    public sealed class JsonTodoStore : ITodoStore
    {
        private const string CorruptedBoardMessage = "Todo Board 文件已损坏，无法读取。";
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

            try
            {
                var json = File.ReadAllText(boardPath, Encoding.UTF8);
                var board = JsonConvert.DeserializeObject<TodoBoard>(json, SerializerSettings);
                if (board == null)
                {
                    throw new InvalidOperationException(CorruptedBoardMessage);
                }

                return Task.FromResult(board);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(CorruptedBoardMessage, ex);
            }
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
            var tempPath = boardPath + ".tmp." + Guid.NewGuid().ToString("N");
            var json = JsonConvert.SerializeObject(board, SerializerSettings);

            try
            {
                File.WriteAllText(tempPath, json, Encoding.UTF8);
                if (File.Exists(boardPath))
                {
                    File.Replace(tempPath, boardPath, null, true);
                }
                else
                {
                    File.Move(tempPath, boardPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

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
