using UniSpring.Logging.Dto;

namespace UniSpring.Logging.Services
{
    public interface ILogConfigurationSource
    {
        LoggingConfiguration Load();
    }
}
