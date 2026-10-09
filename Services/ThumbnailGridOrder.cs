using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoMusicViewer.Services
{
    // Reorder an already loaded grid without replacing its thumbnail objects or
    // touching the filesystem. Back/folder entries stay ahead of the photos.
    internal static class ThumbnailGridOrder
    {
        internal static List<T> Reorder<T>(IEnumerable<T> items, Func<T, bool> isImage,
            Func<T, string> path, Func<List<string>, List<string>> sort)
        {
            var result = new List<T>();
            var images = new Dictionary<string, Queue<T>>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (T item in items)
            {
                if (!isImage(item)) { result.Add(item); continue; }
                string key = path(item);
                if (!images.TryGetValue(key, out var bucket)) images[key] = bucket = new Queue<T>();
                bucket.Enqueue(item);
                paths.Add(key);
            }
            foreach (string key in sort(paths))
            {
                if (!images.TryGetValue(key, out var bucket) || bucket.Count == 0)
                    throw new InvalidOperationException("Grid sort must preserve the photo snapshot.");
                result.Add(bucket.Dequeue());
            }
            if (images.Values.Any(bucket => bucket.Count != 0))
                throw new InvalidOperationException("Grid sort dropped a photo.");
            return result;
        }
    }
}
