namespace SmartWord.Core.Abstractions
{
    public interface INotificationService
    {
        void Info(string message);

        void Error(string message);
    }
}
