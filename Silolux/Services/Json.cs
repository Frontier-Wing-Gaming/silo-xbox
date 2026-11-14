using System.Collections.Generic;
using Windows.Data.Json;

namespace Silolux.Services
{
    /// <summary>Null-tolerant reads over Windows.Data.Json.</summary>
    public static class Json
    {
        public static IJsonValue Get(this JsonObject o, string key)
        {
            IJsonValue v;
            if (o == null || !o.TryGetValue(key, out v) || v.ValueType == JsonValueType.Null) return null;
            return v;
        }

        public static string Str(this JsonObject o, string key, string fallback = null)
        {
            var v = o.Get(key);
            if (v == null) return fallback;
            if (v.ValueType == JsonValueType.String) return v.GetString();
            if (v.ValueType == JsonValueType.Number) return v.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
            return fallback;
        }

        public static double Num(this JsonObject o, string key, double fallback = 0)
        {
            var v = o.Get(key);
            return v != null && v.ValueType == JsonValueType.Number ? v.GetNumber() : fallback;
        }

        public static double? NumOrNull(this JsonObject o, string key)
        {
            var v = o.Get(key);
            return v != null && v.ValueType == JsonValueType.Number ? v.GetNumber() : (double?)null;
        }

        public static bool Bool(this JsonObject o, string key, bool fallback = false)
        {
            var v = o.Get(key);
            return v != null && v.ValueType == JsonValueType.Boolean ? v.GetBoolean() : fallback;
        }

        public static JsonObject Obj(this JsonObject o, string key)
        {
            var v = o.Get(key);
            return v != null && v.ValueType == JsonValueType.Object ? v.GetObject() : null;
        }

        public static IEnumerable<JsonObject> Objects(this JsonObject o, string key)
        {
            var v = o.Get(key);
            if (v == null || v.ValueType != JsonValueType.Array) yield break;
            foreach (var item in v.GetArray())
                if (item.ValueType == JsonValueType.Object) yield return item.GetObject();
        }

        /// <summary>Walks a "/a/b/c" pointer through nested objects.</summary>
        public static JsonObject Path(this JsonObject o, string pointer)
        {
            foreach (var part in pointer.Trim('/').Split('/'))
            {
                o = o.Obj(part);
                if (o == null) return null;
            }
            return o;
        }

        /// <summary>
        /// The viewer's resume point. Catalog reads put it in `user_data`;
        /// some list rows also carry it at the top level.
        /// </summary>
        public static double ResumeSeconds(this JsonObject item) =>
            item.Obj("user_data")?.NumOrNull("position_seconds") ?? item.Num("position_seconds");

        public static JsonArray Strings(params string[] values)
        {
            var a = new JsonArray();
            foreach (var s in values) a.Add(JsonValue.CreateStringValue(s));
            return a;
        }

        public static JsonObject With(this JsonObject o, string key, string value)
        {
            o[key] = JsonValue.CreateStringValue(value);
            return o;
        }

        public static JsonObject With(this JsonObject o, string key, double value)
        {
            o[key] = JsonValue.CreateNumberValue(value);
            return o;
        }

        public static JsonObject With(this JsonObject o, string key, bool value)
        {
            o[key] = JsonValue.CreateBooleanValue(value);
            return o;
        }

        public static JsonObject With(this JsonObject o, string key, IJsonValue value)
        {
            o[key] = value;
            return o;
        }
    }
}
