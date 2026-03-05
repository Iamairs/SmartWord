using SmartWord.Core.Abstractions.Conversation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using SmartWord.Core.Models.Conversation;

// 文件说明：
// 基于 JSON 文件的会话存储实现，负责会话读写、活动会话切换与并发保护。
namespace SmartWord.Services.Storage
{
    /// <summary>
    /// 文件会话存储。
    /// </summary>
    public sealed class FileConversationStore : IConversationStore
    {
        private readonly string _storeFilePath;
        private readonly object _syncRoot = new object();

        /// <summary>
        /// 初始化文件会话存储。
        /// </summary>
        /// <param name="storeFilePath">存储文件路径。</param>
        public FileConversationStore(string storeFilePath)
        {
            _storeFilePath = string.IsNullOrWhiteSpace(storeFilePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "chat.sessions.local.json")
                : storeFilePath;
        }

        /// <summary>
        /// 加载会话列表。
        /// </summary>
        /// <returns>会话只读列表。</returns>
        public Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync()
        {
            lock (_syncRoot)
            {
                // 锁内读取，确保多线程场景下文件读写一致。
                StoreFileModel model = LoadFile();
                var result = new List<ConversationSession>(model.Sessions);
                return Task.FromResult((IReadOnlyList<ConversationSession>)result);
            }
        }

        /// <summary>
        /// 创建新会话并设为活动会话。
        /// </summary>
        public Task<ConversationSession> CreateSessionAsync(string title)
        {
            lock (_syncRoot)
            {
                StoreFileModel model = LoadFile();
                string sessionTitle = string.IsNullOrWhiteSpace(title) ? "新对话" : title.Trim();
                DateTime now = DateTime.UtcNow;

                var session = new ConversationSession
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    Title = sessionTitle,
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };

                for (int i = 0; i < model.Sessions.Count; i++)
                {
                    // 新建会话后重置其他会话活动状态。
                    model.Sessions[i].IsActive = false;
                }

                model.ActiveSessionId = session.SessionId;
                model.Sessions.Insert(0, session);
                SaveFile(model);
                return Task.FromResult(session);
            }
        }

        /// <summary>
        /// 按会话 ID 获取会话。
        /// </summary>
        public Task<ConversationSession> GetSessionAsync(string sessionId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return Task.FromResult<ConversationSession>(null);
                }

                StoreFileModel model = LoadFile();
                for (int i = 0; i < model.Sessions.Count; i++)
                {
                    if (string.Equals(model.Sessions[i].SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(model.Sessions[i]);
                    }
                }

                return Task.FromResult<ConversationSession>(null);
            }
        }

        /// <summary>
        /// 获取当前活动会话。
        /// </summary>
        public Task<ConversationSession> GetActiveSessionAsync()
        {
            lock (_syncRoot)
            {
                StoreFileModel model = LoadFile();
                if (!string.IsNullOrWhiteSpace(model.ActiveSessionId))
                {
                    for (int i = 0; i < model.Sessions.Count; i++)
                    {
                        if (string.Equals(model.Sessions[i].SessionId, model.ActiveSessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult(model.Sessions[i]);
                        }
                    }
                }

                for (int i = 0; i < model.Sessions.Count; i++)
                {
                    if (model.Sessions[i].IsActive)
                    {
                        return Task.FromResult(model.Sessions[i]);
                    }
                }

                return Task.FromResult<ConversationSession>(null);
            }
        }

        /// <summary>
        /// 设置活动会话。
        /// </summary>
        public Task SetActiveSessionAsync(string sessionId)
        {
            lock (_syncRoot)
            {
                StoreFileModel model = LoadFile();
                bool found = false;
                for (int i = 0; i < model.Sessions.Count; i++)
                {
                    bool isTarget = string.Equals(model.Sessions[i].SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
                    model.Sessions[i].IsActive = isTarget;
                    if (isTarget)
                    {
                        model.Sessions[i].UpdatedAtUtc = DateTime.UtcNow;
                        found = true;
                    }
                }

                if (found)
                {
                    model.ActiveSessionId = sessionId;
                    SaveFile(model);
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// 保存会话。
        /// </summary>
        public Task SaveSessionAsync(ConversationSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionId))
            {
                return Task.CompletedTask;
            }

            lock (_syncRoot)
            {
                StoreFileModel model = LoadFile();
                bool updated = false;
                for (int i = 0; i < model.Sessions.Count; i++)
                {
                    if (string.Equals(model.Sessions[i].SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        session.UpdatedAtUtc = DateTime.UtcNow;
                        model.Sessions[i] = session;
                        updated = true;
                        break;
                    }
                }

                if (!updated)
                {
                    // 新会话直接插入头部，便于 UI 侧按最近更新时间展示。
                    session.UpdatedAtUtc = DateTime.UtcNow;
                    model.Sessions.Insert(0, session);
                }

                if (session.IsActive)
                {
                    model.ActiveSessionId = session.SessionId;
                    for (int i = 0; i < model.Sessions.Count; i++)
                    {
                        if (!string.Equals(model.Sessions[i].SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            model.Sessions[i].IsActive = false;
                        }
                    }
                }

                SaveFile(model);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// 从存储文件加载模型。
        /// </summary>
        /// <returns>存储文件模型。</returns>
        private StoreFileModel LoadFile()
        {
            EnsureDirectory();

            if (!File.Exists(_storeFilePath))
            {
                return new StoreFileModel();
            }

            string json = File.ReadAllText(_storeFilePath, Encoding.UTF8);
            StoreFileModel model = Deserialize<StoreFileModel>(json);
            if (model == null)
            {
                model = new StoreFileModel();
            }

            if (model.Sessions == null)
            {
                // 兼容旧文件或异常文件结构。
                model.Sessions = new List<ConversationSession>();
            }

            return model;
        }

        /// <summary>
        /// 将模型写回存储文件。
        /// </summary>
        /// <param name="model">存储模型。</param>
        private void SaveFile(StoreFileModel model)
        {
            EnsureDirectory();
            string json = Serialize(model);
            File.WriteAllText(_storeFilePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 确保存储目录存在。
        /// </summary>
        private void EnsureDirectory()
        {
            string directory = Path.GetDirectoryName(_storeFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// 序列化对象为 JSON。
        /// </summary>
        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// 从 JSON 反序列化对象。
        /// </summary>
        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                return serializer.ReadObject(stream) as T;
            }
        }

        [DataContract]
        private sealed class StoreFileModel
        {
            /// <summary>
            /// 初始化存储文件模型。
            /// </summary>
            public StoreFileModel()
            {
                Sessions = new List<ConversationSession>();
            }

            /// <summary>
            /// 活动会话 ID。
            /// </summary>
            [DataMember(Name = "activeSessionId")]
            public string ActiveSessionId { get; set; }

            /// <summary>
            /// 会话集合。
            /// </summary>
            [DataMember(Name = "sessions")]
            public List<ConversationSession> Sessions { get; set; }
        }
    }
}
