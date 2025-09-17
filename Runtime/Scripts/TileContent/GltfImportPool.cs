using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Logging;

namespace Netherlands3D.Tiles3D
{
    public static class GltfImportPool
    {
        private const int MaxConcurrentImports = 10;
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(MaxConcurrentImports, MaxConcurrentImports);

        private static GltfImport CreateImport()
        {
            var logger = new ConsoleLogger();
            var materialGenerator = new NL3DMaterialGenerator();
            return new GltfImport(null, null, materialGenerator, logger);
        }

        public static async Task<GltfImport> Acquire()
        {
            await semaphore.WaitAsync();
            return CreateImport();
        }

        public static void Release(GltfImport import)
        {
            try
            {
            }
            finally
            {
                semaphore.Release();
            }
        }

        public static void ReleaseAndDispose(GltfImport import)
        {
            try
            {
                if (import != null)
                {
                    try
                    {
                        import.Dispose();
                    }
                    catch
                    {
                        // Ignore dispose errors; we're done with this instance
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
