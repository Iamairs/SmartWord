using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    /// <summary>
    /// 在独立 STA 消息循环中运行真实 Word 测试，模拟 VSTO 宿主的 UI 线程约束。
    /// </summary>
    internal static class StaWordTestHost
    {
        public static Task RunAsync(Func<WordTestSession, Task> testAction)
        {
            if (testAction == null)
            {
                throw new ArgumentNullException(nameof(testAction));
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => RunThread(testAction, completion))
            {
                IsBackground = true,
                Name = "SmartWord.RealWordTests.STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        private static void RunThread(
            Func<WordTestSession, Task> testAction,
            TaskCompletionSource<bool> completion)
        {
            WordTestSession session = null;
            var context = new ApplicationContext();
            var dispatcher = new Control();
            _ = dispatcher.Handle;

            dispatcher.BeginInvoke(new Action(async () =>
            {
                Exception failure = null;
                try
                {
                    session = WordTestSession.Start(dispatcher);
                    await testAction(session);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    try
                    {
                        session?.Dispose();
                    }
                    catch (Exception cleanupException)
                    {
                        failure = failure == null
                            ? cleanupException
                            : new AggregateException(failure, cleanupException);
                    }

                    dispatcher.Dispose();
                    context.ExitThread();

                    if (failure == null)
                    {
                        completion.TrySetResult(true);
                    }
                    else
                    {
                        completion.TrySetException(failure);
                    }
                }
            }));

            System.Windows.Forms.Application.Run(context);
        }
    }
}
