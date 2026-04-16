using SmartWord.OfficeIntegration.WordWrappers;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    /// <summary>
    /// 验证任务级 UndoRecord 回滚只作用于当前活动文档。
    /// </summary>
    public sealed class UndoRecordWrapperTests
    {
        [Fact]
        public void Rollback_UsesActiveDocumentUndo_ToRollbackCurrentDocument()
        {
            var fakeDocument = new FakeWordDocument(@"C:\docs\current.docx");
            var fakeApplication = new FakeWordApplication(fakeDocument);

            using (var wordApplicationWrapper = new WordApplicationWrapper(fakeApplication))
            {
                var undoScope = new UndoRecordWrapper(
                    fakeApplication,
                    wordApplicationWrapper,
                    fakeDocument.FullName);

                undoScope.BeginTransaction("测试回滚");
                undoScope.Rollback();

                Assert.Equal(1, fakeApplication.UndoRecord.StartCustomRecordCount);
                Assert.Equal(1, fakeApplication.UndoRecord.EndCustomRecordCount);
                Assert.Equal(1, fakeDocument.UndoCallCount);
                Assert.Equal(1, fakeDocument.LastUndoTimes);
            }
        }

        [Fact]
        public void Rollback_ActiveDocumentChanged_DoesNotUndoDifferentDocument()
        {
            var initialDocument = new FakeWordDocument(@"C:\docs\initial.docx");
            var switchedDocument = new FakeWordDocument(@"C:\docs\switched.docx");
            var fakeApplication = new FakeWordApplication(initialDocument);

            using (var wordApplicationWrapper = new WordApplicationWrapper(fakeApplication))
            {
                var undoScope = new UndoRecordWrapper(
                    fakeApplication,
                    wordApplicationWrapper,
                    initialDocument.FullName);

                undoScope.BeginTransaction("测试回滚");
                fakeApplication.ActiveDocument = switchedDocument;

                undoScope.Rollback();

                Assert.Equal(1, fakeApplication.UndoRecord.StartCustomRecordCount);
                Assert.Equal(1, fakeApplication.UndoRecord.EndCustomRecordCount);
                Assert.Equal(0, initialDocument.UndoCallCount);
                Assert.Equal(0, switchedDocument.UndoCallCount);
            }
        }

        public sealed class FakeWordApplication
        {
            public FakeWordApplication(FakeWordDocument activeDocument)
            {
                ActiveDocument = activeDocument;
                UndoRecord = new FakeUndoRecord();
            }

            public FakeWordDocument ActiveDocument { get; set; }

            public FakeUndoRecord UndoRecord { get; }
        }

        public sealed class FakeWordDocument
        {
            public FakeWordDocument(string fullName)
            {
                FullName = fullName;
            }

            public string FullName { get; }

            public int UndoCallCount { get; private set; }

            public int LastUndoTimes { get; private set; }

            public bool Undo(int times)
            {
                UndoCallCount++;
                LastUndoTimes = times;
                return true;
            }
        }

        public sealed class FakeUndoRecord
        {
            public int StartCustomRecordCount { get; private set; }

            public int EndCustomRecordCount { get; private set; }

            public void StartCustomRecord(string operationName)
            {
                _ = operationName;
                StartCustomRecordCount++;
            }

            public void EndCustomRecord()
            {
                EndCustomRecordCount++;
            }
        }
    }
}
