using UniSpring.DependencyInjection;

namespace UniSpring.Samples.BasicSetup
{
    public interface IGreetingService
    {
        string Greet(string name);
    }

    [Service]
    public sealed class GreetingService : IGreetingService
    {
        public string Greet(string name)
        {
            return "Hello, " + name + "!";
        }
    }
}
