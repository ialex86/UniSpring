using UniSpring.Logging.Dto;

namespace UniSpring.Logging.Services
{
    public interface ILogSink
    {
        void Write(LogEntry entry);
    }
}
