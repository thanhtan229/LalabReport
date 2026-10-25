using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;

namespace LalabAutoReport.Core.Interfaces;

public interface IOrderGalleryService
{
    Task<OrderGalleryManifest> GetOrderManifestAsync(long orderId, CancellationToken cancellationToken = default);
    Task<OrderGalleryPage> GetOrderGalleryPageAsync(long orderId, string manifestVersion, int offset, int limit, CancellationToken cancellationToken = default);
    Task<ThumbnailResult> GetGalleryThumbnailAsync(long orderId, string manifestVersion, string fileId, CancellationToken cancellationToken = default);
    Task TouchViewedBatchAsync(long orderId, string manifestVersion, IEnumerable<string> fileIds, CancellationToken cancellationToken = default);
}
