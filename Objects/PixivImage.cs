﻿// Pixeval
// Copyright (C) 2019 Dylech30th <decem0730@gmail.com>
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImageMagick;
using Pixeval.Core;
using Pixeval.Data.ViewModel;
using Pixeval.Data.Web.Delegation;
using Pixeval.Persisting;

namespace Pixeval.Objects
{
    public static class PixivImage
    {
        internal static async Task<byte[]> FromUrlInternal(string url)
        {
            var client = HttpClientFactory.PixivImage();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "http://www.pixiv.net");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PixivIOSApp/5.8.7");

            return await client.GetByteArrayAsync(url);
        }

        public static async Task<BitmapImage> FromUrl(string url)
        {
            return FromByteArray(await FromUrlInternal(url));
        }

        public static BitmapImage FromByteArray(byte[] bArr)
        {
            using var memoryStream = new MemoryStream(bArr);
            return FromStream(memoryStream);
        }

        public static BitmapImage FromStream(Stream stream)
        {
            var bmp = new BitmapImage {CreateOptions = BitmapCreateOptions.DelayCreation};
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public static Task<BitmapImage> FromStreamAsync(Stream stream)
        {
            return Task.Run(() => FromStream(stream));
        }

        internal static void SaveBitmapImage(this BitmapImage bitmapImage, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapImage));

            using var fs = new FileStream(path, FileMode.Create);
            encoder.Save(fs);
        }

        public static void CacheImage(byte[] imageBArr, string id, int episode = 0)
        {
            CacheImage(FromByteArray(imageBArr), id, episode);
        }

        public static void CacheImage(BitmapImage bitmapImage, string id, int episode = 0)
        {
            Task.Run(() =>
            {
                var path = Path.Combine(PixevalEnvironment.TempFolder, $"{id}_{episode}.png");
                if (!File.Exists(path)) bitmapImage.SaveBitmapImage(path);
            });
        }

        public static async Task<BitmapImage> GetAndCreateOrLoadFromCache(bool usingCache, string url, string id, int episode = 0)
        {
            if (usingCache)
            {
                var path = Path.Combine(PixevalEnvironment.TempFolder, $"{id}_{episode}.png");
                if (File.Exists(path))
                {
                    var toCache = FromByteArray(await File.ReadAllBytesAsync(path));
                    CacheImage(toCache, id, episode);
                    return toCache;
                }
            }

            var img = await FromUrl(url);
            return img;
        }

        private static IReadOnlyList<Stream> ReadGifZipEntries(Stream stream)
        {
            var dis = new List<Stream>();
            using var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var zipArchiveEntry in zipArchive.Entries)
            {
                using var aStream = zipArchiveEntry.Open();

                var ms = new MemoryStream();
                aStream.CopyTo(ms);
                ms.Position = 0L;
                dis.Add(ms);
            }

            return dis;
        }

        internal static IReadOnlyList<Stream> ReadGifZipEntries(byte[] bArr)
        {
            return ReadGifZipEntries(new MemoryStream(bArr));
        }

        internal static async IAsyncEnumerable<BitmapImage> ReadGifZipBitmapImages(byte[] bArr)
        {
            var lst = await Task.Run(() => ReadGifZipEntries(bArr));

            foreach (var s in lst) yield return await FromStreamAsync(s);
        }

        internal static Task<BitmapImage> GetAndCreateOrLoadFromCacheInternal(string url, string id, int episode = 0)
        {
            return GetAndCreateOrLoadFromCache(Settings.Global.CachingThumbnail, url, id, episode);
        }

        private static Stream MergeGifStream(IReadOnlyList<Stream> streams, IReadOnlyList<long> delay)
        {
            var ms = new MemoryStream();
            using var mCollection = new MagickImageCollection();

            for (var i = 0; i < streams.Count; i++)
            {
                var iStream = streams[i];

                var img = new MagickImage(iStream)
                {
                    AnimationDelay = (int) delay[i]
                };
                mCollection.Add(img);
            }

            var settings = new QuantizeSettings {Colors = 256};
            mCollection.Quantize(settings);
            mCollection.Optimize();
            mCollection.Write(ms, MagickFormat.Gif);
            return ms;
        }

        public static async Task<Stream> GetGifStream(string link, IReadOnlyList<long> delay)
        {
            return MergeGifStream(ReadGifZipEntries(await FromUrlInternal(link)), delay);
        }

        public static async Task<BitmapImage> GetGifBitmap(string link, IReadOnlyList<long> delay)
        {
            return FromStream(MergeGifStream(ReadGifZipEntries(await FromUrlInternal(link)), delay));
        }

        public static Task DownloadIllustsInternal(IEnumerable<Illustration> illustrations, string addIllustratorNamedDirectory = null)
        {
            return DownloadIllusts(illustrations, Settings.Global.DownloadLocation, addIllustratorNamedDirectory);
        }

        public static Task DownloadIllusts(IEnumerable<Illustration> illustrations, string path, string addIllustratorNamedDirectory = null)
        {
            return Task.WhenAll(illustrations.Select(illustration => DownloadIllust(illustration, path, addIllustratorNamedDirectory)));
        }

        internal static async Task DownloadIllustInternal(Illustration illustration, string addIllustratorNamedDirectory = null)
        {
            await DownloadIllust(illustration, Settings.Global.DownloadLocation, addIllustratorNamedDirectory);
        }

        public static async Task DownloadIllust(Illustration illustration, string rootPath, string addIllustratorNamedDirectory = null)
        {
            var path = TextBuffer.GetOrCreateDirectory(addIllustratorNamedDirectory == null ? rootPath : Path.Combine(rootPath, addIllustratorNamedDirectory));

            if (illustration.IsManga)
            {
                await DownloadManga(illustration, path);
            }
            else if (illustration.IsUgoira)
            {
                await DownloadUgoira(illustration, path);
            }
            else
            {
                var url = illustration.Origin;
                await File.WriteAllBytesAsync(Path.Combine(path, $"{illustration.Id}{GetExtension(url)}"), await FromUrlInternal(url));
            }
        }

        public static async Task DownloadUgoira(Illustration illustration, string rootPath)
        {
            if (!illustration.IsUgoira) throw new InvalidOperationException();

            var metadata = await HttpClientFactory.AppApiService.GetUgoiraMetadata(illustration.Id);
            var url = FormatGifZipUrl(metadata.UgoiraMetadataInfo.ZipUrls.Medium);

            await using var img = (MemoryStream) await GetGifStream(url, metadata.UgoiraMetadataInfo.Frames.Select(f => f.Delay / 10).ToList());
            await File.WriteAllBytesAsync(Path.Combine(rootPath, $"{illustration.Id}.gif"), await Task.Run(() => img.ToArray()));
        }

        public static async Task DownloadManga(Illustration illustration, string rootPath)
        {
            if (!illustration.IsManga) throw new InvalidOperationException();

            var mangaDir = TextBuffer.GetOrCreateDirectory(Path.Combine(rootPath, illustration.Id));

            for (var i = 0; i < illustration.MangaMetadata.Length; i++)
            {
                var url = illustration.MangaMetadata[i].Origin;
                await File.WriteAllBytesAsync(Path.Combine(mangaDir, $"{i}{GetExtension(url)}"), await FromUrlInternal(url));
            }
        }

        public static async Task DownloadSpotlight(SpotlightArticle article)
        {
            var root = TextBuffer.GetOrCreateDirectory(Path.Combine(Settings.Global.DownloadLocation, "Spotlight", article.Title));

            var illusts = await PixivClient.Instance.GetArticleWorks(article.Id.ToString());
            var list = new List<Task>();
            foreach (var illust in illusts) list.Add(DownloadIllust(await PixivHelper.IllustrationInfo(illust), root));

            await Task.WhenAll(list);
        }

        private static string GetExtension(string url)
        {
            return TextBuffer.GetExtension(url);
        }

        public static string FormatGifZipUrl(string link)
        {
            return !link.EndsWith("ugoira1920x1080.zip") ? Regex.Replace(link, "ugoira(\\d+)x(\\d+).zip", "ugoira1920x1080.zip") : link;
        }

        public static void AddIllust(this Collection<Illustration> container, Illustration illust)
        {
            if (IllustNotMatchCondition(Settings.Global.ExceptTags, Settings.Global.ContainsTags, illust))
                return;

            if (Settings.Global.SortOnInserting)
                container.AddSorted(illust, IllustrationComparator.Instance);
            else
                container.Add(illust);
        }

        internal static bool IllustNotMatchCondition(ISet<string> exceptTag, ISet<string> containsTag, Illustration illustration)
        {
            return !exceptTag.IsNullOrEmpty() && exceptTag.Any(x => !x.IsNullOrEmpty() && illustration.Tags.Any(i => i.EqualsIgnoreCase(x))) ||
                   !containsTag.IsNullOrEmpty() && containsTag.Any(x => !x.IsNullOrEmpty() && !illustration.Tags.Any(i => i.EqualsIgnoreCase(x))) ||
                   illustration.Bookmark < Settings.Global.MinBookmark;
        }
    }
}