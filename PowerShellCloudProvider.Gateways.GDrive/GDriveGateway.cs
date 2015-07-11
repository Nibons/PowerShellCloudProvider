﻿/*
The MIT License(MIT)

Copyright(c) 2015 IgorSoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IgorSoft.PowerShellCloudProvider.Interface;
using IgorSoft.PowerShellCloudProvider.Interface.Composition;
using IgorSoft.PowerShellCloudProvider.Interface.IO;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v2;
using Google.Apis.Drive.v2.Data;
using Google.Apis.Services;
using Google.Apis.Upload;

using GoogleFile = Google.Apis.Drive.v2.Data.File;

namespace IgorSoft.PowerShellCloudProvider.Gateways.GDrive
{
    [ExportAsAsyncCloudGateway("GDrive")]
    [ExportMetadata(nameof(CloudGatewayMetadata.CloudService), GDriveGateway.SCHEMA)]
    [ExportMetadata(nameof(CloudGatewayMetadata.ServiceUri), GDriveGateway.URL)]
    [ExportMetadata(nameof(CloudGatewayMetadata.ApiAssembly), GDriveGateway.API)]
    [System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
    public sealed class GDriveGateway : IAsyncCloudGateway
    {
        private const string SCHEMA = "gdrive";

        private const string URL = "https://drive.google.com";

        private const string API = "Google.Apis.Drive.v2";

        private const string MIME_TYPE_DIRECTORY = "application/vnd.google-apps.folder";

        private const string MIME_TYPE_FILE = "application/octet-stream";

        private const int RETRIES = 3;

        private class GDriveContext
        {
            public DriveService Service { get; }

            public GDriveContext(DriveService service)
            {
                Service = service;
            }
        }

        private IDictionary<RootName, GDriveContext> contextCache = new Dictionary<RootName, GDriveContext>();

        private async Task<GDriveContext> RequireContext(RootName root, string apiKey = null)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var result = default(GDriveContext);
            if (!contextCache.TryGetValue(root, out result)) {
                var clientSecret = new ClientSecrets() { ClientId = Secrets.CLIENT_ID, ClientSecret = Secrets.CLIENT_SECRET };
                var credentials = await GoogleWebAuthorizationBroker.AuthorizeAsync(clientSecret, new[] { DriveService.Scope.Drive }, root.UserName, System.Threading.CancellationToken.None);
                var service = new DriveService(new BaseClientService.Initializer() { HttpClientInitializer = credentials, ApplicationName = "GDrvTest" });
                contextCache.Add(root, result = new GDriveContext(service));
            }
            return result;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Language", "CSE0003:Use expression-bodied members")]
        [ExportAsBindingRedirect]
        public Func<string, System.Reflection.Assembly> AssemblyRedirect
        {
            get {
                return assemblyName => assemblyName == "System.Net.Http.Primitives, Version=1.5.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
                    ? System.Reflection.Assembly.LoadFrom((new FileInfo(typeof(GDriveGateway).Assembly.Location)).Directory.GetFiles("System.Net.Http.Primitives.dll").First().FullName)
                    : assemblyName == "Newtonsoft.Json, Version=6.0.0.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed"
                    ? System.Reflection.Assembly.LoadFrom((new FileInfo(typeof(GDriveGateway).Assembly.Location)).Directory.GetFiles("Newtonsoft.Json.dll").First().FullName)
                    : null;
            }
        }

        public async Task<DriveInfoContract> GetDriveAsync(RootName root, string apiKey)
        {
            var context = await RequireContext(root, apiKey);

            var item = await AsyncFunc.Retry<About, GoogleApiException>(async () => await context.Service.About.Get().ExecuteAsync(), RETRIES);

            var usedSpace = item.QuotaBytesUsed.HasValue ? item.QuotaBytesUsed ?? 0 + item.QuotaBytesUsedInTrash ?? 0 : (long?)null;
            var freeSpace = item.QuotaBytesTotal.HasValue ? item.QuotaBytesTotal.Value - usedSpace : (long?)null;
            return new DriveInfoContract(item.Name, freeSpace, usedSpace);
        }

        public async Task<RootDirectoryInfoContract> GetRootAsync(RootName root, string apiKey)
        {
            var context = await RequireContext(root, apiKey);

            var item = await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await context.Service.Files.Get("root").ExecuteAsync(), RETRIES);

            return new RootDirectoryInfoContract(item.Id, new DateTimeOffset(item.CreatedDate.Value), new DateTimeOffset(item.ModifiedDate.Value));
        }

        public async Task<IEnumerable<FileSystemInfoContract>> GetChildItemAsync(RootName root, DirectoryId parent)
        {
            var context = await RequireContext(root);

            var childReferences = await AsyncFunc.Retry<ChildList, GoogleApiException>(async () => await context.Service.Children.List(parent.Value).ExecuteAsync(), RETRIES);
            var items = childReferences.Items.Select(async c => await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await context.Service.Files.Get(c.Id).ExecuteAsync(), RETRIES)).ToArray();
            Task.WaitAll(items);

            return items.Select(i => i.Result.ToFileSystemInfoContract());
        }

        public async Task<bool> ClearContentAsync(RootName root, FileId target, Func<FileSystemInfoLocator> locatorResolver)
        {
            var context = await RequireContext(root);

            await AsyncFunc.Retry<IUploadProgress, GoogleApiException>(async () => await context.Service.Files.Update(null, target.Value, new MemoryStream(), MIME_TYPE_FILE).UploadAsync(), RETRIES);

            return true;
        }

        public async Task<Stream> GetContentAsync(RootName root, FileId source)
        {
            var context = await RequireContext(root);

            var itemReference = await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await context.Service.Files.Get(source.Value).ExecuteAsync(), RETRIES);
            //var stream = new MemoryStream(await AsyncFunc.Retry<byte[], GoogleApiException>(async () => await context.Service.HttpClient.GetByteArrayAsync(itemReference.DownloadUrl), RETRIES));
            var stream = await AsyncFunc.Retry<Stream, GoogleApiException>(async () => await context.Service.HttpClient.GetStreamAsync(itemReference.DownloadUrl), RETRIES);

            return stream;
        }

        public async Task<bool> SetContentAsync(RootName root, FileId target, Stream content, IProgress<ProgressValue> progress, Func<FileSystemInfoLocator> locatorResolver)
        {
            var context = await RequireContext(root);

            var itemReference = await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await context.Service.Files.Get(target.Value).ExecuteAsync(), RETRIES);
            var update = context.Service.Files.Update(itemReference, target.Value, content, itemReference.MimeType);
            update.ProgressChanged += p => progress.Report(new ProgressValue((int)p.BytesSent, (int)content.Length));
            await AsyncFunc.Retry<IUploadProgress, GoogleApiException>(async () => await update.UploadAsync(), RETRIES);

            return true;
        }

        public async Task<FileSystemInfoContract> CopyItemAsync(RootName root, FileSystemId source, string copyName, DirectoryId destination, bool recurse)
        {
            var context = await RequireContext(root);

            var copy = new GoogleFile() { Title = copyName };
            if (destination != null)
                copy.Parents = new[] { new ParentReference() { Id = destination.Value } };
            var item = await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await context.Service.Files.Copy(copy, source.Value).ExecuteAsync(), RETRIES);

            return item.ToFileSystemInfoContract();
        }

        public async Task<FileSystemInfoContract> MoveItemAsync(RootName root, FileSystemId source, string moveName, DirectoryId destination, Func<FileSystemInfoLocator> locatorResolver)
        {
            var context = await RequireContext(root);

            var move = new GoogleFile() { Parents = new[] { new ParentReference() { Id = destination.Value } }, Title = moveName };
            var patch = context.Service.Files.Patch(move, source.Value);
            var item = await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await patch.ExecuteAsync(), RETRIES);

            return item.ToFileSystemInfoContract();
        }

        public async Task<DirectoryInfoContract> NewDirectoryItemAsync(RootName root, DirectoryId parent, string name)
        {
            var context = await RequireContext(root);

            var file = new GoogleFile() { Title = name, MimeType = MIME_TYPE_DIRECTORY, Parents = new[] { new ParentReference() { Id = parent.Value } } };
            var item = await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await context.Service.Files.Insert(file).ExecuteAsync(), RETRIES);

            return new DirectoryInfoContract(item.Id, item.Title, new DateTimeOffset(item.CreatedDate.Value), new DateTimeOffset(item.ModifiedDate.Value));
        }

        public async Task<FileInfoContract> NewFileItemAsync(RootName root, DirectoryId parent, string name, Stream content, IProgress<ProgressValue> progress)
        {
            var context = await RequireContext(root);

            var file = new GoogleFile() { Title = name, MimeType = MIME_TYPE_FILE, Parents = new[] { new ParentReference() { Id = parent.Value } } };
            var insert = context.Service.Files.Insert(file, content, MIME_TYPE_FILE);
            insert.ProgressChanged += p => progress.Report(new ProgressValue((int)p.BytesSent, (int)content.Length));
            var upload = await AsyncFunc.Retry<IUploadProgress, GoogleApiException>(async () => await insert.UploadAsync(), RETRIES);
            var item = insert.ResponseBody;

            return new FileInfoContract(item.Id, item.Title, new DateTimeOffset(item.CreatedDate.Value), new DateTimeOffset(item.ModifiedDate.Value), item.FileSize.Value, item.Md5Checksum);
        }

        public async Task<bool> RemoveItemAsync(RootName root, FileSystemId target, bool recurse)
        {
            var context = await RequireContext(root);

            var item = await AsyncFunc.Retry<string, GoogleApiException>(async () => await context.Service.Files.Delete(target.Value).ExecuteAsync(), RETRIES);

            return true;
        }

        public async Task<FileSystemInfoContract> RenameItemAsync(RootName root, FileSystemId target, string newName, Func<FileSystemInfoLocator> locatorResolver)
        {
            var context = await RequireContext(root);

            var patch = context.Service.Files.Patch(new GoogleFile() { Title = newName }, target.Value);
            var item = await AsyncFunc.Retry<GoogleFile, GoogleApiException>(async () => await patch.ExecuteAsync(), RETRIES);

            return item.ToFileSystemInfoContract();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Debugger Display")]
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static string DebuggerDisplay => nameof(GDriveGateway);
    }
}
