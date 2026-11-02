using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Vzor
{
    // flea prices from DrakiaXYZ's SPT-LiveFleaPriceDB, shipped next to the plugin as json.
    // template id -> roubles. loaded once, lazily, on first use.
    internal static class PriceDatabase
    {
        private static Dictionary<string, int> _prices;
        private static bool _loaded;
        private static string _loadedFile;

        public static void EnsureLoaded(string fileName)
        {
            if (_loaded && _loadedFile == fileName) return;

            _loaded = true;
            _loadedFile = fileName;
            _prices = null;

            try
            {
                var dir = Path.GetDirectoryName(typeof(PriceDatabase).Assembly.Location) ?? string.Empty;
                var path = Path.Combine(dir, fileName);

                if (!File.Exists(path))
                {
                    Plugin.Logger.LogWarning("price file missing: " + path + ". falling back to template prices.");
                    return;
                }

                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);

                var map = new Dictionary<string, int>(document.RootElement.GetPropertyCount());
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Number) continue;
                    if (!property.Value.TryGetDouble(out var price)) continue;
                    if (price <= 0d) continue;

                    map[property.Name] = (int)price;
                }

                _prices = map;
                Plugin.Logger.LogInfo("prices loaded: " + map.Count + " entries from " + fileName);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("price file failed to load: " + ex.Message);
            }
        }

        public static bool TryGet(string templateId, out int price)
        {
            price = 0;
            if (_prices == null || string.IsNullOrEmpty(templateId)) return false;
            if (!_prices.TryGetValue(templateId, out var value) || value <= 0) return false;

            price = value;
            return true;
        }
    }
}
