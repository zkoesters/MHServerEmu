namespace MHServerEmu
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // We set the console title here because if we were to make a GUI it would have to set its title separately.
            Console.Title = $"MHServerEmu ({ServerApp.VersionInfo})";
            await ServerApp.Instance.RunAsync();
        }
    }
}
