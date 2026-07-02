using Xamarin.Android.AssemblyStore;

namespace AsmBlobTool
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var file  = args[0];
            var store = new AssemblyStoreExplorer(file, keepStoreInMemory: true);
            var path  = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileName(file) + " Assemblies " + DateTime.Now.ToString("yyyy.MM.dd HH.mm.ss"));

            Directory.CreateDirectory(path);
            foreach (var i in store.Assemblies)
            {
                i.ExtractImage(path);
            }
        }
    }
}
