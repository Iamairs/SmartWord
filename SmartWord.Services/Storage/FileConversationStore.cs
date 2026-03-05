using SmartWord.Core.Abstractions.Conversation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using SmartWord.Core.Models.Conversation;

namespace SmartWord.Services.Storage
{
    public sealed class FileConversationStore : IConversationStore
    {
        private readonly string _storeFilePath;
        private readonly object _syncRoot = new object();

        public FileConversationStore(string storeFilePath)
        {
            _storeFilePath = string.IsNullOrWhiteSpace(storeFilePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "chat.sessions.local.json")
                : storeFilePath;
        }

        public Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync()
        {
            lock (_syncRoot)
            {
                StoreFileModel model = LoadFile();
                var result = new List<ConversationSession>(model.Sessions);
                return Task.FromResult((IReadOnlyList<ConversationSession>)result);
            }
        }

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
                    model.Sessions[i].IsActive = false;
                }

                model.ActiveSessionId = session.SessionId;
                model.Sessions.Insert(0, session);
                SaveFile(model);
                return Task.FromResult(session);
            }
        }

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
                model.Sessions = new List<ConversationSession>();
            }

            return model;
        }

        private void SaveFile(StoreFileModel model)
        {
            EnsureDirectory();
            string json = Serialize(model);
            File.WriteAllText(_storeFilePath, json, Encoding.UTF8);
        }

        private void EnsureDirectory()
        {
            string directory = Path.GetDirectoryName(_storeFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

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
            public StoreFileModel()
            {
                Sessions = new List<ConversationSession>();
            }

            [DataMember(Name = "activeSessionId")]
            public string ActiveSessionId { get; set; }

            [DataMember(Name = "sessions")]
            public List<ConversationSession> Sessions { get; set; }
        }
    }
}
