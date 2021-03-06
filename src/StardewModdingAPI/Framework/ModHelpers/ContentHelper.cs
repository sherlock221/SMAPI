using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Exceptions;
using StardewValley;
using xTile;
using xTile.Format;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.ModHelpers
{
    /// <summary>Provides an API for loading content assets.</summary>
    internal class ContentHelper : BaseHelper, IContentHelper
    {
        /*********
        ** Properties
        *********/
        /// <summary>SMAPI's underlying content manager.</summary>
        private readonly SContentManager ContentManager;

        /// <summary>The absolute path to the mod folder.</summary>
        private readonly string ModFolderPath;

        /// <summary>The path to the mod's folder, relative to the game's content folder (e.g. "../Mods/ModName").</summary>
        private readonly string ModFolderPathFromContent;

        /// <summary>The friendly mod name for use in errors.</summary>
        private readonly string ModName;

        /// <summary>Encapsulates monitoring and logging for a given module.</summary>
        private readonly IMonitor Monitor;


        /*********
        ** Accessors
        *********/
        /// <summary>The game's current locale code (like <c>pt-BR</c>).</summary>
        public string CurrentLocale => this.ContentManager.GetLocale();

        /// <summary>The game's current locale as an enum value.</summary>
        public LocalizedContentManager.LanguageCode CurrentLocaleConstant => this.ContentManager.GetCurrentLanguage();

        /// <summary>The observable implementation of <see cref="AssetEditors"/>.</summary>
        internal ObservableCollection<IAssetEditor> ObservableAssetEditors { get; } = new ObservableCollection<IAssetEditor>();

        /// <summary>The observable implementation of <see cref="AssetLoaders"/>.</summary>
        internal ObservableCollection<IAssetLoader> ObservableAssetLoaders { get; } = new ObservableCollection<IAssetLoader>();

        /// <summary>Interceptors which provide the initial versions of matching content assets.</summary>
        public IList<IAssetLoader> AssetLoaders => this.ObservableAssetLoaders;

        /// <summary>Interceptors which edit matching content assets after they're loaded.</summary>
        public IList<IAssetEditor> AssetEditors => this.ObservableAssetEditors;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="contentManager">SMAPI's underlying content manager.</param>
        /// <param name="modFolderPath">The absolute path to the mod folder.</param>
        /// <param name="modID">The unique ID of the relevant mod.</param>
        /// <param name="modName">The friendly mod name for use in errors.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public ContentHelper(SContentManager contentManager, string modFolderPath, string modID, string modName, IMonitor monitor)
            : base(modID)
        {
            this.ContentManager = contentManager;
            this.ModFolderPath = modFolderPath;
            this.ModName = modName;
            this.ModFolderPathFromContent = this.GetRelativePath(contentManager.FullRootDirectory, modFolderPath);
            this.Monitor = monitor;
        }

        /// <summary>Load content from the game folder or mod folder (if not already cached), and return it. When loading a <c>.png</c> file, this must be called outside the game's draw loop.</summary>
        /// <typeparam name="T">The expected data type. The main supported types are <see cref="Texture2D"/> and dictionaries; other types may be supported by the game's content pipeline.</typeparam>
        /// <param name="key">The asset key to fetch (if the <paramref name="source"/> is <see cref="ContentSource.GameContent"/>), or the local path to a content file relative to the mod folder.</param>
        /// <param name="source">Where to search for a matching content asset.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        /// <exception cref="ContentLoadException">The content asset couldn't be loaded (e.g. because it doesn't exist).</exception>
        public T Load<T>(string key, ContentSource source = ContentSource.ModFolder)
        {
            SContentLoadException GetContentError(string reasonPhrase) => new SContentLoadException($"{this.ModName} failed loading content asset '{key}' from {source}: {reasonPhrase}.");

            this.AssertValidAssetKeyFormat(key);
            try
            {
                switch (source)
                {
                    case ContentSource.GameContent:
                        return this.ContentManager.Load<T>(key);

                    case ContentSource.ModFolder:
                        // get file
                        FileInfo file = this.GetModFile(key);
                        if (!file.Exists)
                            throw GetContentError($"there's no matching file at path '{file.FullName}'.");

                        // get asset path
                        string assetPath = this.GetModAssetPath(key, file.FullName);

                        // try cache
                        if (this.ContentManager.IsLoaded(assetPath))
                            return this.ContentManager.Load<T>(assetPath);

                        // load content
                        switch (file.Extension.ToLower())
                        {
                            // XNB file
                            case ".xnb":
                                {
                                    T asset = this.ContentManager.Load<T>(assetPath);
                                    if (asset is Map)
                                        this.FixLocalMapTilesheets(asset as Map, key);
                                    return asset;
                                }

                            // unpacked map
                            case ".tbin":
                                {
                                    // validate
                                    if (typeof(T) != typeof(Map))
                                        throw GetContentError($"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Map)}'.");

                                    // fetch & cache
                                    FormatManager formatManager = FormatManager.Instance;
                                    Map map = formatManager.LoadMap(file.FullName);
                                    this.FixLocalMapTilesheets(map, key);

                                    // inject map
                                    this.ContentManager.Inject(assetPath, map);
                                    return (T)(object)map;
                                }

                            // unpacked image
                            case ".png":
                                // validate
                                if (typeof(T) != typeof(Texture2D))
                                    throw GetContentError($"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Texture2D)}'.");

                                // fetch & cache
                                using (FileStream stream = File.OpenRead(file.FullName))
                                {
                                    Texture2D texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
                                    texture = this.PremultiplyTransparency(texture);
                                    this.ContentManager.Inject(assetPath, texture);
                                    return (T)(object)texture;
                                }

                            default:
                                throw GetContentError($"unknown file extension '{file.Extension}'; must be one of '.png', '.tbin', or '.xnb'.");
                        }

                    default:
                        throw GetContentError($"unknown content source '{source}'.");
                }
            }
            catch (Exception ex) when (!(ex is SContentLoadException))
            {
                throw new SContentLoadException($"{this.ModName} failed loading content asset '{key}' from {source}.", ex);
            }
        }

        /// <summary>Get the underlying key in the game's content cache for an asset. This can be used to load custom map tilesheets, but should be avoided when you can use the content API instead. This does not validate whether the asset exists.</summary>
        /// <param name="key">The asset key to fetch (if the <paramref name="source"/> is <see cref="ContentSource.GameContent"/>), or the local path to a content file relative to the mod folder.</param>
        /// <param name="source">Where to search for a matching content asset.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        public string GetActualAssetKey(string key, ContentSource source = ContentSource.ModFolder)
        {
            switch (source)
            {
                case ContentSource.GameContent:
                    return this.ContentManager.NormaliseAssetName(key);

                case ContentSource.ModFolder:
                    FileInfo file = this.GetModFile(key);
                    return this.ContentManager.NormaliseAssetName(this.GetModAssetPath(key, file.FullName));

                default:
                    throw new NotSupportedException($"Unknown content source '{source}'.");
            }
        }

        /// <summary>Remove an asset from the content cache so it's reloaded on the next request. This will reload core game assets if needed, but references to the former asset will still show the previous content.</summary>
        /// <param name="key">The asset key to invalidate in the content folder.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        /// <returns>Returns whether the given asset key was cached.</returns>
        public bool InvalidateCache(string key)
        {
            this.Monitor.Log($"Requested cache invalidation for '{key}'.", LogLevel.Trace);
            string actualKey = this.GetActualAssetKey(key, ContentSource.GameContent);
            return this.ContentManager.InvalidateCache((otherKey, type) => otherKey.Equals(actualKey, StringComparison.InvariantCultureIgnoreCase));
        }

        /// <summary>Remove all assets of the given type from the cache so they're reloaded on the next request. <b>This can be a very expensive operation and should only be used in very specific cases.</b> This will reload core game assets if needed, but references to the former assets will still show the previous content.</summary>
        /// <typeparam name="T">The asset type to remove from the cache.</typeparam>
        /// <returns>Returns whether any assets were invalidated.</returns>
        public bool InvalidateCache<T>()
        {
            this.Monitor.Log($"Requested cache invalidation for all assets of type {typeof(T)}. This is an expensive operation and should be avoided if possible.", LogLevel.Trace);
            return this.ContentManager.InvalidateCache((key, type) => typeof(T).IsAssignableFrom(type));
        }

        /*********
        ** Private methods
        *********/
        /// <summary>Fix the tilesheets for a map loaded from the mod folder.</summary>
        /// <param name="map">The map whose tilesheets to fix.</param>
        /// <param name="mapKey">The map asset key within the mod folder.</param>
        /// <exception cref="ContentLoadException">The map tilesheets could not be loaded.</exception>
        /// <remarks>
        /// The game's logic for tilesheets in <see cref="Game1.setGraphicsForSeason"/> is a bit specialised. It boils down to this:
        ///  * If the location is indoors or the desert, or the image source contains 'path' or 'object', it's loaded as-is relative to the <c>Content</c> folder.
        ///  * Else it's loaded from <c>Content\Maps</c> with a seasonal prefix.
        /// 
        /// That logic doesn't work well in our case, mainly because we have no location metadata at this point.
        /// Instead we use a more heuristic approach: check relative to the map file first, then relative to
        /// <c>Content\Maps</c>, then <c>Content</c>. If the image source filename contains a seasonal prefix, we try
        /// for a seasonal variation and then an exact match.
        /// 
        /// While that doesn't exactly match the game logic, it's close enough that it's unlikely to make a difference.
        /// </remarks>
        private void FixLocalMapTilesheets(Map map, string mapKey)
        {
            // check map info
            if (!map.TileSheets.Any())
                return;
            mapKey = this.ContentManager.NormaliseAssetName(mapKey); // Mono's Path.GetDirectoryName doesn't handle Windows dir separators
            string relativeMapFolder = Path.GetDirectoryName(mapKey) ?? ""; // folder path containing the map, relative to the mod folder

            // fix tilesheets
            foreach (TileSheet tilesheet in map.TileSheets)
            {
                string imageSource = tilesheet.ImageSource;

                // get seasonal name (if applicable)
                string seasonalImageSource = null;
                if (Game1.currentSeason != null)
                {
                    string filename = Path.GetFileName(imageSource);
                    bool hasSeasonalPrefix =
                        filename.StartsWith("spring_", StringComparison.CurrentCultureIgnoreCase)
                        || filename.StartsWith("summer_", StringComparison.CurrentCultureIgnoreCase)
                        || filename.StartsWith("fall_", StringComparison.CurrentCultureIgnoreCase)
                        || filename.StartsWith("winter_", StringComparison.CurrentCultureIgnoreCase);
                    if (hasSeasonalPrefix && !filename.StartsWith(Game1.currentSeason + "_"))
                    {
                        string dirPath = imageSource.Substring(0, imageSource.LastIndexOf(filename, StringComparison.CurrentCultureIgnoreCase));
                        seasonalImageSource = $"{dirPath}{Game1.currentSeason}_{filename.Substring(filename.IndexOf("_", StringComparison.CurrentCultureIgnoreCase) + 1)}";
                    }
                }

                // load best match
                try
                {
                    string key =
                        this.TryLoadTilesheetImageSource(relativeMapFolder, seasonalImageSource)
                        ?? this.TryLoadTilesheetImageSource(relativeMapFolder, imageSource);
                    if (key != null)
                    {
                        tilesheet.ImageSource = key;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    throw new ContentLoadException($"The '{imageSource}' tilesheet couldn't be loaded relative to either map file or the game's content folder.", ex);
                }

                // none found
                throw new ContentLoadException($"The '{imageSource}' tilesheet couldn't be loaded relative to either map file or the game's content folder.");
            }
        }

        /// <summary>Load a tilesheet image source if the file exists.</summary>
        /// <param name="relativeMapFolder">The folder path containing the map, relative to the mod folder.</param>
        /// <param name="imageSource">The tilesheet image source to load.</param>
        /// <returns>Returns the loaded asset key (if it was loaded successfully).</returns>
        /// <remarks>See remarks on <see cref="FixLocalMapTilesheets"/>.</remarks>
        private string TryLoadTilesheetImageSource(string relativeMapFolder, string imageSource)
        {
            if (imageSource == null)
                return null;

            // check relative to map file
            {
                string localKey = Path.Combine(relativeMapFolder, imageSource);
                FileInfo localFile = this.GetModFile(localKey);
                if (localFile.Exists)
                {
                    try
                    {
                        this.Load<Texture2D>(localKey);
                    }
                    catch (Exception ex)
                    {
                        throw new ContentLoadException($"The local '{imageSource}' tilesheet couldn't be loaded.", ex);
                    }

                    return this.GetActualAssetKey(localKey);
                }
            }

            // check relative to content folder
            {
                foreach (string candidateKey in new[] { imageSource, $@"Maps\{imageSource}" })
                {
                    string contentKey = candidateKey.EndsWith(".png")
                        ? candidateKey.Substring(0, imageSource.Length - 4)
                        : candidateKey;

                    try
                    {
                        this.Load<Texture2D>(contentKey, ContentSource.GameContent);
                        return contentKey;
                    }
                    catch
                    {
                        // ignore file-not-found errors
                        // TODO: while it's useful to suppress a asset-not-found error here to avoid
                        // confusion, this is a pretty naive approach. Even if the file doesn't exist,
                        // the file may have been loaded through an IAssetLoader which failed. So even
                        // if the content file doesn't exist, that doesn't mean the error here is a
                        // content-not-found error. Unfortunately XNA doesn't provide a good way to
                        // detect the error type.
                        if (this.GetContentFolderFile(contentKey).Exists)
                            throw;
                    }
                }
            }

            // not found
            return null;
        }

        /// <summary>Assert that the given key has a valid format.</summary>
        /// <param name="key">The asset key to check.</param>
        /// <exception cref="ArgumentException">The asset key is empty or contains invalid characters.</exception>
        [SuppressMessage("ReSharper", "UnusedParameter.Local", Justification = "Parameter is only used for assertion checks by design.")]
        private void AssertValidAssetKeyFormat(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("The asset key or local path is empty.");
            if (key.Intersect(Path.GetInvalidPathChars()).Any())
                throw new ArgumentException("The asset key or local path contains invalid characters.");
        }

        /// <summary>Get a file from the mod folder.</summary>
        /// <param name="path">The asset path relative to the mod folder.</param>
        private FileInfo GetModFile(string path)
        {
            // try exact match
            path = Path.Combine(this.ModFolderPath, this.ContentManager.NormalisePathSeparators(path));
            FileInfo file = new FileInfo(path);

            // try with default extension
            if (!file.Exists && file.Extension.ToLower() != ".xnb")
            {
                FileInfo result = new FileInfo(path + ".xnb");
                if (result.Exists)
                    file = result;
            }

            return file;
        }

        /// <summary>Get a file from the game's content folder.</summary>
        /// <param name="key">The asset key.</param>
        private FileInfo GetContentFolderFile(string key)
        {
            // get file path
            string path = Path.Combine(this.ContentManager.FullRootDirectory, key);
            if (!path.EndsWith(".xnb"))
                path += ".xnb";

            // get file
            return new FileInfo(path);
        }

        /// <summary>Get the asset path which loads a mod folder through a content manager.</summary>
        /// <param name="localPath">The file path relative to the mod's folder.</param>
        /// <param name="absolutePath">The absolute file path.</param>
        private string GetModAssetPath(string localPath, string absolutePath)
        {
#if SMAPI_FOR_WINDOWS
            // XNA doesn't allow absolute asset paths, so get a path relative to the content folder
            return Path.Combine(this.ModFolderPathFromContent, localPath);
#else
            // MonoGame is weird about relative paths on Mac, but allows absolute paths
            return absolutePath;
#endif
        }

        /// <summary>Get a directory path relative to a given root.</summary>
        /// <param name="rootPath">The root path from which the path should be relative.</param>
        /// <param name="targetPath">The target file path.</param>
        private string GetRelativePath(string rootPath, string targetPath)
        {
            // convert to URIs
            Uri from = new Uri(rootPath + "/");
            Uri to = new Uri(targetPath + "/");
            if (from.Scheme != to.Scheme)
                throw new InvalidOperationException($"Can't get path for '{targetPath}' relative to '{rootPath}'.");

            // get relative path
            return Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString())
                .Replace(Path.DirectorySeparatorChar == '/' ? '\\' : '/', Path.DirectorySeparatorChar); // use correct separator for platform
        }

        /// <summary>Premultiply a texture's alpha values to avoid transparency issues in the game. This is only possible if the game isn't currently drawing.</summary>
        /// <param name="texture">The texture to premultiply.</param>
        /// <returns>Returns a premultiplied texture.</returns>
        /// <remarks>Based on <a href="https://gist.github.com/Layoric/6255384">code by Layoric</a>.</remarks>
        private Texture2D PremultiplyTransparency(Texture2D texture)
        {
            // validate
            if (Context.IsInDrawLoop)
                throw new NotSupportedException("Can't load a PNG file while the game is drawing to the screen. Make sure you load content outside the draw loop.");

            // process texture
            SpriteBatch spriteBatch = Game1.spriteBatch;
            GraphicsDevice gpu = Game1.graphics.GraphicsDevice;
            using (RenderTarget2D renderTarget = new RenderTarget2D(Game1.graphics.GraphicsDevice, texture.Width, texture.Height))
            {
                // create blank render target to premultiply
                gpu.SetRenderTarget(renderTarget);
                gpu.Clear(Color.Black);

                // multiply each color by the source alpha, and write just the color values into the final texture
                spriteBatch.Begin(SpriteSortMode.Immediate, new BlendState
                {
                    ColorDestinationBlend = Blend.Zero,
                    ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue,
                    AlphaDestinationBlend = Blend.Zero,
                    AlphaSourceBlend = Blend.SourceAlpha,
                    ColorSourceBlend = Blend.SourceAlpha
                });
                spriteBatch.Draw(texture, texture.Bounds, Color.White);
                spriteBatch.End();

                // copy the alpha values from the source texture into the final one without multiplying them
                spriteBatch.Begin(SpriteSortMode.Immediate, new BlendState
                {
                    ColorWriteChannels = ColorWriteChannels.Alpha,
                    AlphaDestinationBlend = Blend.Zero,
                    ColorDestinationBlend = Blend.Zero,
                    AlphaSourceBlend = Blend.One,
                    ColorSourceBlend = Blend.One
                });
                spriteBatch.Draw(texture, texture.Bounds, Color.White);
                spriteBatch.End();

                // release GPU
                gpu.SetRenderTarget(null);

                // extract premultiplied data
                Color[] data = new Color[texture.Width * texture.Height];
                renderTarget.GetData(data);

                // unset texture from GPU to regain control
                gpu.Textures[0] = null;

                // update texture with premultiplied data
                texture.SetData(data);
            }

            return texture;
        }
    }
}
