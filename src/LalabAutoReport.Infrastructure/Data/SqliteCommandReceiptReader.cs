using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.Infrastructure.Data;

public class SqliteCommandReceiptReader : IOperationLookupStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteCommandReceiptReader(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<OperationLookupResult> LookupOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ReceiptDto>(@"
            SELECT operation_id, capability, entity_type, entity_id, result_json
            FROM command_receipts
            WHERE operation_id = @OperationId;
        ", new { OperationId = operationId });

        if (row != null)
        {
            return new OperationLookupResult(
                OperationLookupStatus.Committed,
                row.capability,
                row.entity_type,
                row.entity_id,
                row.result_json);
        }

        return new OperationLookupResult(OperationLookupStatus.NotFound);
    }

    private class ReceiptDto
    {
        public string operation_id { get; set; } = string.Empty;
        public string capability { get; set; } = string.Empty;
        public string entity_type { get; set; } = string.Empty;
        public long entity_id { get; set; }
        public string result_json { get; set; } = string.Empty;
    }
}
