using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Logging;

namespace Netherlands3D.Tiles3D
{
    /// <summary>
    /// Limits concurrent glTF parsing/instantiation so we do not spawn dozens of heavy
    /// <see cref="GltfImport"/> tasks at once. Web browsers typically cap simultaneous
    /// HTTP requests per host at about six, and WebGL builds struggle when more than a
    /// handful of tiles parse in parallel. The pool therefore keeps the number of active
    /// imports manageable while still allowing some concurrency.
    /// </summary>
    public static class GltfImportPool
    {
        /// <summary>
        /// Hard cap on parallel imports. Ten keeps us safely above browser request limits
        /// (≈6 per host) while preventing excessive CPU/memory spikes during parsing.
        /// </summary>
        private const int MaxConcurrentImports = 10;
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(MaxConcurrentImports, MaxConcurrentImports);

        private static GltfImport CreateImport()
        {
            var logger = new ConsoleLogger();
            var materialGenerator = new NL3DMaterialGenerator();
            return new GltfImport(null, null, materialGenerator, logger);
        }

        /// <summary>
        /// Blocks until the pool grants a slot, then returns a freshly configured
        /// <see cref="GltfImport"/> ready for loading.
        /// </summary>
        public static async Task<GltfImport> Acquire()
        {
            await semaphore.WaitAsync();
            return CreateImport();
        }

        /// <summary>
        /// Returns the importer to the pool without disposing it; call when you want to
        /// reuse the instance for another tile load.
        /// </summary>
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

        /// <summary>
        /// Returns the slot and disposes the importer to free parser-side buffers. Use this
        /// when an import failed or when we explicitly opt-in to releasing the memory.
        /// </summary>
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
