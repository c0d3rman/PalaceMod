using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.IO;
using System.Reflection;
using System.Linq;

namespace PalaceMod
{
    public abstract class BaseManager : IManager
    {
        protected Plugin Plugin { get; private set; }

        public virtual void Awake(Plugin plugin)
        {
            Plugin = plugin;
        }

        public virtual void Start() { }

        public virtual void Update() { }

        public virtual void OnDestroy() { }

        /// <summary>
        /// Loads a sprite from an embedded resource in the Assets folder.
        /// </summary>
        /// <param name="resourceName">The name of the resource file (e.g., "dice_icon.png")</param>
        /// <param name="pixelsPerUnit">Pixels per unit for the sprite. Default is 100.</param>
        /// <param name="pivot">Pivot point for the sprite. Default is center (0.5, 0.5).</param>
        /// <returns>A Sprite created from the embedded resource</returns>
        /// <exception cref="FileNotFoundException">Thrown when the resource cannot be found</exception>
        /// <exception cref="InvalidOperationException">Thrown when the resource cannot be loaded as an image</exception>
        protected Sprite LoadSpriteFromAssets(string resourceName, float pixelsPerUnit = 100f, Vector2? pivot = null)
        {
            if (string.IsNullOrEmpty(resourceName))
                throw new ArgumentNullException(nameof(resourceName), "Resource name cannot be null or empty");

            var assembly = Assembly.GetExecutingAssembly();
            string fullResourceName = $"{MyPluginInfo.PLUGIN_GUID}.Assets.{resourceName}";
            if (!assembly.GetManifestResourceNames().Contains(fullResourceName))
            {
                string availableResources = string.Join(", ", assembly.GetManifestResourceNames());
                throw new FileNotFoundException(
                    $"Resource '{resourceName}' not found. Full resource name '{fullResourceName}' not found in assembly. " +
                    $"Available resources: {availableResources}",
                    resourceName);
            }

            using (Stream stream = assembly.GetManifestResourceStream(fullResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException($"Failed to open resource stream for '{fullResourceName}'");

                byte[] fileData;
                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    fileData = ms.ToArray();
                }

                if (fileData.Length == 0)
                    throw new InvalidOperationException($"Resource '{fullResourceName}' is empty");

                Texture2D texture = new Texture2D(2, 2);
                if (!texture.LoadImage(fileData))
                    throw new InvalidOperationException($"Failed to load image data from resource '{fullResourceName}'");

                return Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    pivot ?? new Vector2(0.5f, 0.5f),
                    pixelsPerUnit
                );
            }
        }
    }
}
