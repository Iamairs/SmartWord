using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SmartWord.OfficeIntegration.ComInterop;
using SmartWord.OfficeIntegration.WordWrappers;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    /// <summary>
    /// 验证 COM 生命周期基础设施的释放顺序、调度关闭和只读重试语义。
    /// </summary>
    public sealed class ComLifecycleTests
    {
        [Fact]
        public void Dispose_登记重复对象_按逆序且仅释放一次()
        {
            var releasedOwners = new List<string>();
            var scope = new ComScope((value, owner) => releasedOwners.Add(owner));

            var first = new object();
            scope.Track(first, "first");
            scope.Track(new object(), "second");
            scope.Track(new object(), "third");
            scope.Track(first, "duplicate");

            scope.Dispose();
            scope.Dispose();

            Assert.Equal(new[] { "third", "second", "first" }, releasedOwners);
        }

        [Fact]
        public void Track_作用域已释放_抛出ObjectDisposedException()
        {
            var scope = new ComScope((value, owner) => { });
            scope.Dispose();

            Assert.Throws<ObjectDisposedException>(() => scope.Track(new object(), "late"));
        }

        [Fact]
        public void ReleaseOwned_普通托管对象_直接忽略()
        {
            ComObjectReleaser.ReleaseOwned(new object(), "managed");
        }

        [Fact]
        public void ExecuteRead_前两次WordBusy_第三次返回结果()
        {
            var attempts = 0;
            var delays = new List<int>();

            var result = ComBusyRetryPolicy.ExecuteRead(
                () =>
                {
                    attempts++;
                    if (attempts < 3)
                    {
                        throw new COMException("Word busy", ComBusyRetryPolicy.RpcCallRejected);
                    }

                    return 42;
                },
                "test_read",
                maxAttempts: 3,
                initialDelayMilliseconds: 10,
                wait: delays.Add);

            Assert.Equal(42, result);
            Assert.Equal(3, attempts);
            Assert.Equal(new[] { 10, 20 }, delays);
        }

        [Fact]
        public void ExecuteRead_非Busy异常_不进行重试()
        {
            var attempts = 0;

            Assert.Throws<COMException>(() => ComBusyRetryPolicy.ExecuteRead<int>(
                () =>
                {
                    attempts++;
                    throw new COMException("not busy", unchecked((int)0x80004005));
                },
                "test_read",
                wait: delay => { }));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task GetParagraphCountAsync_活动文档暂时Busy_有限重试后返回段落数()
        {
            var application = new BusyWordApplication(2, 7);
            using (var wrapper = new WordApplicationWrapper(application, useDirectInvoke: true))
            {
                var paragraphCount = await wrapper.GetParagraphCountAsync();

                Assert.Equal(7, paragraphCount);
                Assert.Equal(3, application.ActiveDocumentAccessCount);
            }
        }

        [Fact]
        public async Task InvokeAsync_包装器已释放_拒绝新调度()
        {
            var wrapper = new WordApplicationWrapper(new object(), useDirectInvoke: true);
            wrapper.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => wrapper.InvokeAsync(() => 1));
        }

        [Fact]
        public async Task InvokeAsync_写操作遇到WordBusy_不会自动重放()
        {
            var attempts = 0;
            using (var wrapper = new WordApplicationWrapper(new object(), useDirectInvoke: true))
            {
                await Assert.ThrowsAsync<COMException>(() => wrapper.InvokeAsync<int>(() =>
                {
                    attempts++;
                    throw new COMException("Word busy", ComBusyRetryPolicy.RpcServerCallRetryLater);
                }));
            }

            Assert.Equal(1, attempts);
        }

        public sealed class BusyWordApplication
        {
            private readonly int _busyAttempts;
            private readonly FakeWordDocument _document;

            public BusyWordApplication(int busyAttempts, int paragraphCount)
            {
                _busyAttempts = busyAttempts;
                _document = new FakeWordDocument(paragraphCount);
            }

            public int ActiveDocumentAccessCount { get; private set; }

            public FakeWordDocument ActiveDocument
            {
                get
                {
                    ActiveDocumentAccessCount++;
                    if (ActiveDocumentAccessCount <= _busyAttempts)
                    {
                        throw new COMException("Word busy", ComBusyRetryPolicy.RpcCallRejected);
                    }

                    return _document;
                }
            }
        }

        public sealed class FakeWordDocument
        {
            public FakeWordDocument(int paragraphCount)
            {
                Paragraphs = new FakeParagraphCollection(paragraphCount);
            }

            public FakeParagraphCollection Paragraphs { get; }
        }

        public sealed class FakeParagraphCollection
        {
            public FakeParagraphCollection(int count)
            {
                Count = count;
            }

            public int Count { get; }
        }
    }
}
