namespace UniSpring.Logging.Services
{
    public interface ILogConfigPathProvider
    {
        string GetPath();

        /// <summary>True when the file should exist (editor, or an explicit override).</summary>
        bool IsRequired { get; }
    }
}
