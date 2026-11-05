using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    [Serializable]
    public class KeyValue
    {
        public string key;
        public string value;
    }

    [Serializable]
    public class ObjectEffect
    {
        public string type;
        public List<KeyValue> props = new List<KeyValue>();
    }

    /// <summary>The original object definition (.obj text from Objects.pak) this prefab was built from.</summary>
    public class SkyTrackObject : MonoBehaviour
    {
        public string gamePath;
        public string definitionPath;
        public string kind;
        public List<KeyValue> properties = new List<KeyValue>();
        public List<ObjectEffect> effects = new List<ObjectEffect>();
        public List<Texture2D> lightmaps = new List<Texture2D>();
        public Texture2D bumpTexture;

        public string Get(string key, string fallback = null)
        {
            foreach (var kv in properties)
                if (kv.key == key) return kv.value;
            return fallback;
        }

        public float GetFloat(string key, float fallback)
        {
            var v = Get(key);
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
        }
    }
}
