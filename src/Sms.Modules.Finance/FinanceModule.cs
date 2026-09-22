using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Audit;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Finance;

public sealed record FeePaymentResponse(
    Guid Id, Guid TenantId, Guid StudentId, string? StudentName, string? ClassLabel, string FeeType,
    decimal Amount, string? Method, string? Ref, DateTime Date,
    Guid? InvoiceId = null, string? HeadId = null);

public sealed record CreateFeePaymentRequest(
    Guid StudentId, string? StudentName, string? ClassLabel, string? FeeType, decimal Amount, string? Method, string? Ref,
    Guid? InvoiceId = null, string? HeadId = null, string? HeadName = null, string? Mode = null, string? Cls = null,
    Guid? IdempotencyKey = null);

/// <summary>
/// Thrown when a client-supplied IdempotencyKey matches an existing FeePayments row whose
/// Amount/InvoiceId differ from the current request. Signals a genuine conflict — not a safe
/// replay — so callers must surface a 409 rather than returning the (different) stored payment.
/// </summary>
public sealed class IdempotencyKeyConflictException()
    : Exception("This idempotency key was already used for a different payment");

public sealed class FeeRepository(IDbConnectionFactory factory, IAuditLogger auditLogger) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"TenantId\", \"StudentId\", \"StudentName\", \"ClassLabel\", \"FeeType\", \"Amount\", \"Method\", \"Ref\", \"Date\", \"InvoiceId\", \"HeadId\"";

    private sealed record FeePaymentCreateRow(
        Guid Id, Guid TenantId, Guid StudentId, string? StudentName, string? ClassLabel, string FeeType,
        decimal Amount, string? Method, string? Ref, DateTime Date, Guid? InvoiceId, string? HeadId, bool WasCreated);

    public async Task<FeePaymentResponse?> CreateAsync(
        Guid tenantId, CreateFeePaymentRequest r, Guid? actorUserId, CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var args = new
        {
            TenantId = tenantId,
            r.StudentId,
            StudentName = r.StudentName,
            ClassLabel = string.IsNullOrWhiteSpace(r.ClassLabel) ? r.Cls : r.ClassLabel,
            FeeType = string.IsNullOrWhiteSpace(r.FeeType) ? r.HeadName : r.FeeType,
            r.Amount,
            Method = string.IsNullOrWhiteSpace(r.Method) ? r.Mode : r.Method,
            r.Ref,
            r.InvoiceId,
            r.HeadId,
            r.IdempotencyKey,
        };
        var row = await conn.QuerySingleOrDefaultAsync<FeePaymentCreateRow>(new CommandDefinition(
            FunctionCallSql("dbo.FeePayment_Create", args),
            args,
            tx,
            commandType: CommandType.Text,
            cancellationToken: ct));

        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        if (!row.WasCreated && (row.Amount != r.Amount || (r.InvoiceId is { } reqInvoiceId && row.InvoiceId != reqInvoiceId)))
        {
            /* IdempotencyKey matched an existing row, but the request's Amount/InvoiceId don't —
               a stale-replay/reuse, not a safe retry. Reject instead of silently returning the
               (different) stored payment. */
            await tx.RollbackAsync(ct);
            throw new IdempotencyKeyConflictException();
        }

        if (row.WasCreated)
        {
            await auditLogger.LogAsync(conn, tx, new AuditEntry(
                tenantId, actorUserId, "FeePayment.Recorded", "Fees", "FeePayment", row.Id.ToString(),
                AfterData: new { row.Id, row.Amount, row.Method, row.StudentId }), ct);
        }

        await tx.CommitAsync(ct);
        return new FeePaymentResponse(
            row.Id, row.TenantId, row.StudentId, row.StudentName, row.ClassLabel, row.FeeType,
            row.Amount, row.Method, row.Ref, row.Date, row.InvoiceId, row.HeadId);
    }

    public Task<IReadOnlyList<FeePaymentResponse>> ListAsync(Guid? studentId, CancellationToken ct = default) =>
        QueryInlineAsync<FeePaymentResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"FeePayments\" WHERE (@studentId::uuid IS NULL OR \"StudentId\" = @studentId::uuid) ORDER BY \"Date\" DESC",
            new { studentId }, ct);

    public async Task<FeePaymentResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<FeePaymentResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"FeePayments\" WHERE \"Id\" = @id", new { id }, ct)).FirstOrDefault();
}

// ---- Fee invoices (student/parent bills) ----

/// <summary>One fee-head line making up an invoice's total, e.g. "Transport Fee — 500".
/// Amount is snapshotted at invoice-generation time — later Fee Head renames/amount edits
/// must never change an already-generated invoice's lines.</summary>
public sealed record FeeInvoiceLineResponse(Guid? HeadId, string HeadName, decimal Amount, string? Description = null);

/// <summary>Input to CreateWithLinesAsync — same shape as the response, kept as a separate
/// type so the generation path isn't coupled to the wire-serialized response record.</summary>
public sealed record FeeInvoiceLineInput(Guid? HeadId, string HeadName, decimal Amount, string? Description = null);

public sealed record FeeInvoiceResponse(
    Guid Id, Guid TenantId, Guid StudentId, string? Period, DateTime? DueDate, decimal Amount,
    string Status, DateTime? PaidOn, string? Method,
    string? StudentName = null, string? ClassLabel = null, string? AdmissionNo = null,
    string? Grade = null, int? AvatarHue = null, string? PhotoUrl = null,
    decimal PaidAmount = 0, IReadOnlyList<FeeInvoiceLineResponse> Lines = null!);

public sealed record CreateFeeInvoiceRequest(Guid StudentId, string? Period, DateTime? DueDate, decimal Amount);

/// <summary>Body for POST /fees/invoices/{id}/pay (CRM Record payment).</summary>
public sealed record PayFeeInvoiceRequest(
    decimal? Amount,
    string? Method,
    string? Mode,
    string? Ref,
    string? StudentName,
    string? ClassLabel,
    string? Cls,
    string? FeeType,
    string? HeadId,
    string? HeadName,
    Guid? IdempotencyKey = null);

public sealed class FeeInvoiceRepository(IDbConnectionFactory factory, IAuditLogger auditLogger) : BaseRepository(factory)
{
    private static int _paidAmountReady;

    private const string InvoiceCols =
        "i.\"Id\", i.\"TenantId\", i.\"StudentId\", i.\"Period\", i.\"DueDate\", i.\"Amount\", i.\"Status\", i.\"PaidOn\", i.\"Method\", COALESCE(i.\"PaidAmount\", 0) AS \"PaidAmount\"";
    private const string StudentJoinCols =
        "s.\"Name\" AS \"StudentName\", s.\"ClassLabel\", s.\"AdmissionNo\", s.\"Grade\", s.\"AvatarHue\", s.\"PhotoUrl\"";
    private const string SelectJoined =
        $"SELECT {InvoiceCols}, {StudentJoinCols} FROM \"dbo\".\"FeeInvoices\" i LEFT JOIN \"dbo\".\"Students\" s ON s.\"Id\" = i.\"StudentId\"";

    // The COL_LENGTH/ALTER TABLE self-healing migration this method used to do on SQL Server is
    // gone: db/postgres/04_tables.sql's "FeeInvoices" already has PaidAmount (decimal(18,2)
    // DEFAULT 0 NOT NULL) from day one, so there's nothing to add. The one-time phantom-"paid"
    // backfill below is real data correction (not a schema check), so it's kept.
    private async Task EnsurePaidAmountColumnAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _paidAmountReady, 1, 0) != 0) return;
        try
        {
            /* Reopen phantom "paid" with PaidAmount 0 (false full-pay from earlier bug). Uses a
               LEFT JOIN in the FROM subquery (not a plain join) so invoices for students with
               ZERO payment rows at all are still candidates for reopening -- an inner join here
               would silently skip exactly those. */
            await ExecuteInlineAsync(
                """
                WITH pay AS (
                    SELECT "StudentId", CAST(sum("Amount") AS decimal(18,2)) AS "Paid"
                    FROM "dbo"."FeePayments"
                    GROUP BY "StudentId"
                )
                UPDATE "dbo"."FeeInvoices" i
                SET
                    "PaidAmount" = CASE
                        WHEN COALESCE(x."Paid", 0) > i."Amount" THEN i."Amount"
                        ELSE COALESCE(x."Paid", 0)
                    END,
                    "Status" = CASE
                        WHEN COALESCE(x."Paid", 0) >= i."Amount" AND i."Amount" > 0 THEN 'paid'
                        WHEN COALESCE(x."Paid", 0) > 0 THEN 'partial'
                        ELSE 'due'
                    END,
                    "PaidOn" = CASE
                        WHEN COALESCE(x."Paid", 0) >= i."Amount" AND i."Amount" > 0
                            THEN COALESCE(i."PaidOn", now()::date)
                        ELSE NULL
                    END,
                    "Method" = CASE WHEN COALESCE(x."Paid", 0) > 0 THEN i."Method" ELSE NULL END
                FROM (
                    SELECT i2."Id", p."Paid"
                    FROM "dbo"."FeeInvoices" i2
                    LEFT JOIN pay p ON p."StudentId" = i2."StudentId"
                ) x
                WHERE x."Id" = i."Id"
                  AND i."Status" = 'paid'
                  AND COALESCE(i."PaidAmount", 0) = 0
                  AND i."Amount" > 0
                """,
                null, ct);
        }
        catch
        {
            Interlocked.Exchange(ref _paidAmountReady, 0);
            throw;
        }
    }

    public async Task<FeeInvoiceResponse?> CreateAsync(Guid tenantId, CreateFeeInvoiceRequest r, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        var core = await QuerySingleProcAsync<FeeInvoiceCore>("dbo.FeeInvoice_Create",
            new { TenantId = tenantId, r.StudentId, r.Period, r.DueDate, r.Amount }, ct);
        return core is null ? null : await GetAsync(core.Id, ct);
    }

    /// <summary>Invoice generation's path: creates the invoice AND its per-fee-head line items in one
    /// transaction. Amount is the sum of the lines — the caller must not pass a total that disagrees
    /// with them. Manual invoice creation (<see cref="CreateAsync"/>) is untouched and keeps
    /// producing invoices with no lines, exactly as before.</summary>
    public async Task<FeeInvoiceResponse?> CreateWithLinesAsync(
        Guid tenantId, Guid studentId, string? period, DateTime? dueDate,
        IReadOnlyList<FeeInvoiceLineInput> lines, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        if (lines.Count == 0) return null;
        var total = lines.Sum(l => l.Amount);
        if (total <= 0) return null;

        var invoiceId = Guid.NewGuid();
        await using var conn = await Factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO "dbo"."FeeInvoices" ("Id", "TenantId", "StudentId", "Period", "DueDate", "Amount")
                VALUES (@invoiceId, @tenantId, @studentId, @period, @dueDate, @total)
                """,
                new { invoiceId, tenantId, studentId, period, dueDate, total }, tx, cancellationToken: ct));

            foreach (var line in lines)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO "dbo"."FeeInvoiceLines" ("Id", "TenantId", "InvoiceId", "FeeHeadId", "FeeHeadName", "Amount", "FeeHeadDescription")
                    VALUES (@id, @tenantId, @invoiceId, @headId, @headName, @amount, @description)
                    """,
                    new
                    {
                        id = Guid.NewGuid(), tenantId, invoiceId,
                        headId = line.HeadId, headName = line.HeadName, amount = line.Amount,
                        description = line.Description,
                    }, tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetAsync(invoiceId, ct);
    }

    public async Task<FeeInvoiceResponse?> MarkPaidAsync(Guid id, string method, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        var core = await QuerySingleProcAsync<FeeInvoiceCore>("dbo.FeeInvoice_MarkPaid", new { Id = id, Method = method }, ct);
        if (core is null) return null;
        await ExecuteInlineAsync(
            """UPDATE "dbo"."FeeInvoices" SET "PaidAmount" = "Amount" WHERE "Id" = @id""",
            new { id }, ct);
        return await GetAsync(core.Id, ct);
    }

    public async Task<FeeInvoiceResponse?> MarkStatusAsync(
        Guid id, string status, string? method, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        await ExecuteInlineAsync(
            """
            UPDATE "dbo"."FeeInvoices"
            SET "Status" = @status,
                "Method" = COALESCE(@method, "Method"),
                "PaidOn" = CASE WHEN @status = 'paid' THEN now()::date ELSE "PaidOn" END
            WHERE "Id" = @id AND "Status" <> 'paid'
            """,
            new { id, status, method }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Add a payment toward the invoice; sets status paid/partial from accumulated PaidAmount.</summary>
    public async Task<FeeInvoiceResponse?> ApplyPaymentAsync(
        Guid id, decimal amount, string method, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        await ExecuteInlineAsync(
            """
            UPDATE "dbo"."FeeInvoices"
            SET
                "PaidAmount" = COALESCE("PaidAmount", 0) + @amount,
                "Method" = @method,
                "Status" = CASE
                    WHEN COALESCE("PaidAmount", 0) + @amount >= "Amount" THEN 'paid'
                    ELSE 'partial'
                END,
                "PaidOn" = CASE
                    WHEN COALESCE("PaidAmount", 0) + @amount >= "Amount" THEN now()::date
                    ELSE "PaidOn"
                END
            WHERE "Id" = @id
              AND "Status" <> 'paid'
            """,
            new { id, amount, method }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Fast-path lookup used by callers to short-circuit before any invoice-state checks.</summary>
    public async Task<FeePaymentResponse?> GetPaymentByIdempotencyKeyAsync(
        Guid tenantId, Guid key, CancellationToken ct = default) =>
        (await QueryInlineAsync<FeePaymentResponse>(
            """
            SELECT "Id", "TenantId", "StudentId", "StudentName", "ClassLabel", "FeeType", "Amount", "Method", "Ref", "Date", "InvoiceId", "HeadId"
            FROM "dbo"."FeePayments" WHERE "TenantId" = @tenantId AND "IdempotencyKey" = @key
            """,
            new { tenantId, key }, ct)).FirstOrDefault();

    /// <summary>
    /// WasCreated is true only when THIS call is the one that actually inserted the FeePayments row.
    /// It is false whenever a payment is returned via an idempotency-key conflict path — either this
    /// call's own top-of-transaction lookup found a row a concurrent call already committed, or this
    /// call's INSERT lost the unique-index race to a concurrent call. Callers must only fire
    /// once-per-payment side effects (e.g. the guardian payment notification) when WasCreated is true,
    /// or a verify/webhook race for the same payment fires that side effect twice.
    /// </summary>
    public async Task<(FeePaymentResponse? Payment, bool WasCreated)> RecordInvoicePaymentAsync(
        Guid tenantId, Guid invoiceId, CreateFeePaymentRequest req, decimal amount, string method,
        Guid? actorUserId, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        await using var conn = await Factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            if (req.IdempotencyKey is { } key)
            {
                var existing = await conn.QuerySingleOrDefaultAsync<FeePaymentResponse>(new CommandDefinition(
                    """
                    SELECT "Id", "TenantId", "StudentId", "StudentName", "ClassLabel", "FeeType", "Amount", "Method", "Ref", "Date", "InvoiceId", "HeadId"
                    FROM "dbo"."FeePayments" WHERE "TenantId" = @tenantId AND "IdempotencyKey" = @key
                    """,
                    new { tenantId, key }, tx, cancellationToken: ct));
                if (existing is not null)
                {
                    if (existing.InvoiceId != invoiceId || existing.Amount != amount)
                    {
                        /* Same IdempotencyKey, different invoice/amount — reject instead of
                           returning the (different) payment that was already recorded under it. */
                        throw new IdempotencyKeyConflictException();
                    }
                    await tx.CommitAsync(ct);
                    return (existing, false);
                }
            }

            var inv = await conn.QuerySingleOrDefaultAsync<InvoiceLockRow>(
                new CommandDefinition(
                    """
                    SELECT "Id", "StudentId", "Amount", "Status", COALESCE("PaidAmount", 0) AS "PaidAmount"
                    FROM "dbo"."FeeInvoices"
                    WHERE "Id" = @invoiceId
                    FOR UPDATE
                    """,
                    new { invoiceId }, tx, cancellationToken: ct));
            if (inv is null)
            {
                await tx.RollbackAsync(ct);
                return (null, false);
            }

            var remaining = Math.Max(0, inv.Amount - inv.PaidAmount);
            if (remaining <= 0 || string.Equals(inv.Status, "paid", StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(ct);
                return (null, false);
            }

            var payId = Guid.NewGuid();
            var classLabel = string.IsNullOrWhiteSpace(req.ClassLabel) ? req.Cls : req.ClassLabel;
            var feeType = string.IsNullOrWhiteSpace(req.FeeType)
                ? (string.IsNullOrWhiteSpace(req.HeadName) ? "academic" : req.HeadName)
                : req.FeeType;
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO "dbo"."FeePayments" ("Id", "TenantId", "StudentId", "StudentName", "ClassLabel", "FeeType", "Amount", "Method", "Ref", "Date", "InvoiceId", "HeadId", "IdempotencyKey", "CreatedAt")
                    VALUES (@payId, @tenantId, @StudentId, @StudentName, @classLabel, @feeType, @amount, @method, @Ref, now()::date, @invoiceId, @HeadId, @IdempotencyKey, now())
                    """,
                    new
                    {
                        payId,
                        tenantId,
                        inv.StudentId,
                        req.StudentName,
                        classLabel,
                        feeType,
                        amount,
                        method,
                        req.Ref,
                        invoiceId,
                        req.HeadId,
                        req.IdempotencyKey,
                    }, tx, cancellationToken: ct));
            }
            catch (PostgresException sqlEx) when (
                req.IdempotencyKey is not null && sqlEx.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                /* Concurrent request with the same IdempotencyKey won the race and inserted first —
                   fall back to returning that row instead of surfacing the unique-index violation. */
                var raced = await conn.QuerySingleOrDefaultAsync<FeePaymentResponse>(new CommandDefinition(
                    """
                    SELECT "Id", "TenantId", "StudentId", "StudentName", "ClassLabel", "FeeType", "Amount", "Method", "Ref", "Date", "InvoiceId", "HeadId"
                    FROM "dbo"."FeePayments" WHERE "TenantId" = @tenantId AND "IdempotencyKey" = @key
                    """,
                    new { tenantId, key = req.IdempotencyKey }, tx, cancellationToken: ct));
                if (raced is not null && (raced.InvoiceId != invoiceId || raced.Amount != amount))
                {
                    /* Concurrent request won the race, but for a different invoice/amount —
                       reject rather than returning that (different) payment as if it were ours. */
                    throw new IdempotencyKeyConflictException();
                }
                await tx.CommitAsync(ct);
                return (raced, false);
            }

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE "dbo"."FeeInvoices"
                SET
                    "PaidAmount" = COALESCE("PaidAmount", 0) + @amount,
                    "Method" = @method,
                    "Status" = CASE
                        WHEN COALESCE("PaidAmount", 0) + @amount >= "Amount" THEN 'paid'
                        ELSE 'partial'
                    END,
                    "PaidOn" = CASE
                        WHEN COALESCE("PaidAmount", 0) + @amount >= "Amount" THEN now()::date
                        ELSE "PaidOn"
                    END
                WHERE "Id" = @invoiceId
                  AND "Status" <> 'paid'
                """,
                new { invoiceId, amount, method }, tx, cancellationToken: ct));

            var payment = await conn.QuerySingleOrDefaultAsync<FeePaymentResponse>(new CommandDefinition(
                """
                SELECT "Id", "TenantId", "StudentId", "StudentName", "ClassLabel", "FeeType", "Amount", "Method", "Ref", "Date", "InvoiceId", "HeadId"
                FROM "dbo"."FeePayments" WHERE "Id" = @payId
                """,
                new { payId }, tx, cancellationToken: ct));

            await auditLogger.LogAsync(conn, tx, new AuditEntry(
                tenantId, actorUserId, "FeePayment.Recorded", "Fees", "FeePayment", payId.ToString(),
                AfterData: new { Id = payId, InvoiceId = invoiceId, Amount = amount, Method = method }), ct);

            await tx.CommitAsync(ct);
            return (payment, true);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private const string LineCols = "\"InvoiceId\", \"FeeHeadId\" AS \"HeadId\", \"FeeHeadName\" AS \"HeadName\", \"Amount\", \"FeeHeadDescription\" AS \"Description\"";

    public async Task<FeeInvoiceResponse?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        var row = (await QueryInlineAsync<FeeInvoiceSqlRow>(
            $"{SelectJoined} WHERE i.\"Id\" = @id", new { id }, ct))
            .FirstOrDefault();
        if (row is null) return null;
        var lines = await QueryInlineAsync<FeeInvoiceLineRow>(
            $"SELECT {LineCols} FROM \"dbo\".\"FeeInvoiceLines\" WHERE \"InvoiceId\" = @id ORDER BY \"CreatedAt\"",
            new { id }, ct);
        return row.ToResponse(lines.Select(l => l.ToResponse()).ToList());
    }

    public async Task<IReadOnlyList<FeeInvoiceResponse>> ListAsync(Guid? studentId, CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        var rows = await QueryInlineAsync<FeeInvoiceSqlRow>(
            $"{SelectJoined} WHERE (@studentId::uuid IS NULL OR i.\"StudentId\" = @studentId::uuid) ORDER BY i.\"DueDate\" DESC",
            new { studentId }, ct);
        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.Id).ToList();
        var allLines = await QueryInlineAsync<FeeInvoiceLineRow>(
            $"SELECT {LineCols} FROM \"dbo\".\"FeeInvoiceLines\" WHERE \"InvoiceId\" = ANY(@ids) ORDER BY \"CreatedAt\"",
            new { ids }, ct);
        var byInvoice = allLines
            .GroupBy(l => l.InvoiceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FeeInvoiceLineResponse>)g.Select(l => l.ToResponse()).ToList());

        return rows.Select(r => r.ToResponse(
            byInvoice.TryGetValue(r.Id, out var ls) ? ls : [])).ToList();
    }

    public async Task<bool> ExistsForStudentPeriodAsync(Guid studentId, string period, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<int>(
            """SELECT 1 FROM "dbo"."FeeInvoices" WHERE "StudentId" = @studentId AND "Period" = @period LIMIT 1""",
            new { studentId, period }, ct);
        return rows.Count > 0;
    }

    /// <summary>
    /// Every distinct Period already used by ANY invoice for this academic year (Period is the
    /// free-text "{AcademicYear} {Term}" string GenerateInvoicesAsync builds — there is no
    /// separate stored Term/period column, so this is the only source of truth for "which
    /// periods has this tenant actually generated for this year"). Used to backfill NEWLY
    /// created students onto periods that already exist, without inventing a period name.
    /// DueDate is the latest one recorded against that period, for reuse on the new invoice.
    /// </summary>
    public async Task<IReadOnlyList<FeeInvoicePeriodRow>> ListPeriodsForYearAsync(
        string academicYear, CancellationToken ct = default)
    {
        // Postgres LIKE's escape char is '\' (the SQL standard default), not T-SQL's '[...]'.
        var escaped = academicYear.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var prefix = escaped + " %";
        return await QueryInlineAsync<FeeInvoicePeriodRow>(
            """
            SELECT "Period", MAX("DueDate") AS "DueDate"
            FROM "dbo"."FeeInvoices"
            WHERE "Period" LIKE @prefix
            GROUP BY "Period"
            """,
            new { prefix }, ct);
    }

    /// <summary>One round trip for every (StudentId, Period) pair already invoiced among the
    /// given students — batched existence check for ApplyExistingFeeStructureAsync's backfill,
    /// so it never issues one ExistsForStudentPeriodAsync call per student. Carries InvoiceId/
    /// Amount/PaidAmount too, so the backfill can decide create vs. recalculate-if-unpaid vs.
    /// leave-alone without a second round trip.</summary>
    public async Task<IReadOnlyList<StudentInvoicePeriodRow>> ListExistingPeriodsByStudentAsync(
        IReadOnlyList<Guid> studentIds, CancellationToken ct = default)
    {
        if (studentIds.Count == 0) return [];
        await EnsurePaidAmountColumnAsync(ct);
        return await QueryInlineAsync<StudentInvoicePeriodRow>(
            """SELECT "StudentId", "Period", "Id" AS "InvoiceId", "Amount", "PaidAmount" FROM "dbo"."FeeInvoices" WHERE "StudentId" = ANY(@ids)""",
            new { ids = studentIds }, ct);
    }

    /// <summary>
    /// Recalculates an invoice's lines/total to the freshly computed values — ONLY while it is
    /// still fully unpaid. The WHERE PaidAmount = 0 guard is the actual safety mechanism (not
    /// just a pre-check the caller already did): if a payment lands between the caller reading
    /// this invoice and this call, the UPDATE simply matches zero rows and this returns false,
    /// so a part-/fully-paid invoice can never be silently mutated by a race. Replaces (not
    /// appends) FeeInvoiceLines, so re-running with the same computed lines is a no-op amount-
    /// wise and never duplicates a line.
    /// </summary>
    public async Task<bool> ReplaceLinesIfUnpaidAsync(
        Guid tenantId, Guid invoiceId, decimal newAmount, IReadOnlyList<FeeInvoiceLineInput> lines,
        CancellationToken ct = default)
    {
        await EnsurePaidAmountColumnAsync(ct);
        await using var conn = await Factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var updated = await conn.ExecuteAsync(new CommandDefinition(
                """UPDATE "dbo"."FeeInvoices" SET "Amount" = @newAmount WHERE "Id" = @invoiceId AND "PaidAmount" = 0""",
                new { newAmount, invoiceId }, tx, cancellationToken: ct));
            if (updated == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            await conn.ExecuteAsync(new CommandDefinition(
                """DELETE FROM "dbo"."FeeInvoiceLines" WHERE "InvoiceId" = @invoiceId""",
                new { invoiceId }, tx, cancellationToken: ct));

            foreach (var line in lines)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO "dbo"."FeeInvoiceLines" ("Id", "TenantId", "InvoiceId", "FeeHeadId", "FeeHeadName", "Amount", "FeeHeadDescription")
                    VALUES (@id, @tenantId, @invoiceId, @headId, @headName, @amount, @description)
                    """,
                    new
                    {
                        id = Guid.NewGuid(), tenantId, invoiceId,
                        headId = line.HeadId, headName = line.HeadName, amount = line.Amount,
                        description = line.Description,
                    }, tx, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Cross-tenant fee rollup (call under platform elevation so RLS does not filter peers out).
    /// </summary>
    public Task<IReadOnlyList<FeeTenantSummaryRow>> SummarizeByTenantsAsync(
        IReadOnlyList<Guid> tenantIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (tenantIds.Count == 0)
            return Task.FromResult<IReadOnlyList<FeeTenantSummaryRow>>([]);
        var json = System.Text.Json.JsonSerializer.Serialize(tenantIds);
        return QueryProcAsync<FeeTenantSummaryRow>("dbo.Fee_SummaryByTenants",
            new { TenantIds = json, From = from.ToDateTime(TimeOnly.MinValue), To = to.ToDateTime(TimeOnly.MinValue) }, ct);
    }

    private sealed class InvoiceLockRow
    {
        public Guid Id { get; set; }
        public Guid StudentId { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; } = "due";
        public decimal PaidAmount { get; set; }
    }

    private sealed record FeeInvoiceCore(
        Guid Id, Guid TenantId, Guid StudentId, string? Period, DateTime? DueDate, decimal Amount,
        string Status, DateTime? PaidOn, string? Method);

    private sealed class FeeInvoiceLineRow
    {
        public Guid InvoiceId { get; set; }
        public Guid? HeadId { get; set; }
        public string HeadName { get; set; } = "";
        public decimal Amount { get; set; }
        public string? Description { get; set; }

        public FeeInvoiceLineResponse ToResponse() => new(HeadId, HeadName, Amount, Description);
    }

    private sealed class FeeInvoiceSqlRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid StudentId { get; set; }
        public string? Period { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; } = "due";
        public DateTime? PaidOn { get; set; }
        public string? Method { get; set; }
        public string? StudentName { get; set; }
        public string? ClassLabel { get; set; }
        public string? AdmissionNo { get; set; }
        public string? Grade { get; set; }
        public int? AvatarHue { get; set; }
        public string? PhotoUrl { get; set; }
        public decimal PaidAmount { get; set; }

        public FeeInvoiceResponse ToResponse(IReadOnlyList<FeeInvoiceLineResponse> lines) => new(
            Id, TenantId, StudentId, Period, DueDate, Amount, Status, PaidOn, Method,
            StudentName, ClassLabel, AdmissionNo, Grade, AvatarHue, PhotoUrl, PaidAmount, lines);
    }
}

public sealed record FeeTenantSummaryRow(
    Guid TenantId, string Name, decimal Collected, decimal Outstanding, int PaymentCount, int InvoiceCount);

/// <summary>CRM Fees collection KPIs — GET /v1/fees/reports/summary.</summary>
public sealed record FeeReportByClass(string Label, decimal Value, int N);
public sealed record FeeReportByMode(string Label, decimal Value);
public sealed record FeeReportLatestPayment(
    Guid Id, Guid StudentId, string? StudentName, string? Cls, decimal Amount,
    string? Mode, string? Ref, DateTime Date, string? HeadId = null);
public sealed record FeeReportSummaryResponse(
    decimal CollectedToday, decimal CollectedTerm, decimal Outstanding, int Defaulters,
    decimal BilledTerm, decimal Pct,
    IReadOnlyList<FeeReportByClass> ByClass,
    IReadOnlyList<FeeReportByMode> ByMode,
    FeeReportLatestPayment? LatestPayment);

public sealed record GenerateFeeInvoicesRequest(
    string AcademicYear,
    string Term,
    DateTime? DueDate,
    IReadOnlyList<string>? Grades,
    IReadOnlyList<string>? Classes);

public sealed record GenerateFeeInvoicesResponse(int Created, int Recalculated = 0);

/// <summary>Explicit "Sync/Apply Existing Fees" request: reconciles EVERY current student in
/// the given class/grade against whatever periods already exist for the tenant/academic-year of
/// the currently active Fee Structure(s) — for students created before this feature shipped, or
/// before a period existed for their class. Same rules, same idempotency, same batching as the
/// automatic on-create backfill; no academic_year/term to pick, since periods are read from what
/// already exists rather than invented.</summary>
public sealed record ReconcileFeeInvoicesRequest(IReadOnlyList<string>? Grades, IReadOnlyList<string>? Classes);

public sealed record FeeInvoicePeriodRow(string Period, DateTime? DueDate);
public sealed record StudentInvoicePeriodRow(Guid StudentId, string Period, Guid InvoiceId, decimal Amount, decimal PaidAmount);

// ---- Payslips (HR/payroll) ----
public sealed record PayslipResponse(
    Guid Id, Guid TenantId, Guid UserId, string? Month, int Year, decimal Gross, decimal Deductions, decimal Net, string Status,
    decimal Basic, decimal Hra, decimal Allowances, decimal Epf, decimal ProfTax, decimal OtherDeductions);
public sealed record CreatePayslipRequest(Guid UserId, string? Month, int Year, decimal Gross, decimal Deductions, decimal Net);

public sealed class PayslipRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"TenantId\", \"UserId\", \"Month\", \"Year\", \"Gross\", \"Deductions\", \"Net\", \"Status\", " +
        "\"Basic\", \"Hra\", \"Allowances\", \"Epf\", \"ProfTax\", \"OtherDeductions\"";

    public Task<PayslipResponse?> CreateAsync(Guid tenantId, CreatePayslipRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<PayslipResponse>("dbo.Payslip_Create",
            new { TenantId = tenantId, r.UserId, r.Month, r.Year, r.Gross, r.Deductions, r.Net }, ct);

    public Task<IReadOnlyList<PayslipResponse>> ListAsync(Guid tenantId, Guid userId, CancellationToken ct = default) =>
        QueryInlineAsync<PayslipResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"Payslips\" WHERE \"TenantId\" = @tenantId AND \"UserId\" = @userId ORDER BY \"Year\" DESC, \"Month\" DESC",
            new { tenantId, userId }, ct);

    /// Replace any payslip for this user/period and publish payroll figures (mobile payslip feed).
    public async Task PublishForUserAsync(
        Guid tenantId, Guid userId, string? month, int year,
        decimal basic, decimal hra, decimal allowances, decimal epf, decimal profTax, decimal otherDeductions,
        decimal gross, decimal deductions, decimal net, string status = "paid", CancellationToken ct = default)
    {
        var slipStatus = string.IsNullOrWhiteSpace(status) ? "pending" : status.Trim().ToLowerInvariant();
        await ExecuteInlineAsync(
            "DELETE FROM \"dbo\".\"Payslips\" WHERE \"TenantId\"=@tenantId AND \"UserId\"=@userId AND \"Month\"=@month AND \"Year\"=@year",
            new { tenantId, userId, month, year }, ct);
        await ExecuteInlineAsync(
            """
            INSERT INTO "dbo"."Payslips" ("TenantId", "UserId", "Month", "Year", "Gross", "Deductions", "Net", "Status",
                "Basic", "Hra", "Allowances", "Epf", "ProfTax", "OtherDeductions")
            VALUES (@tenantId, @userId, @month, @year, @gross, @deductions, @net, @status,
                @basic, @hra, @allowances, @epf, @profTax, @otherDeductions)
            """,
            new
            {
                tenantId, userId, month, year, gross, deductions, net, status = slipStatus,
                basic, hra, allowances, epf, profTax, otherDeductions,
            }, ct);
    }
}

// ---- Payroll (salary master + monthly run/approve) ----
public sealed record SalaryProfileResponse(
    Guid TenantId, string PersonType, Guid PersonId, decimal BasicSalary, decimal Hra, decimal Allowances,
    decimal Epf, decimal ProfTax, decimal OtherDeductions, string? Uan,
    string? BankHolder, string? BankAccount, string? BankName, string? Ifsc, string? BankBranch);

public sealed record UpsertSalaryProfileRequest(
    decimal BasicSalary, decimal Hra, decimal Allowances, decimal Epf, decimal ProfTax, decimal OtherDeductions,
    string? Uan, string? BankHolder, string? BankAccount, string? BankName, string? Ifsc, string? BankBranch);

// ---- Salary structure templates keyed by role/designation ----
public sealed record SalaryStructureResponse(
    Guid TenantId, string PersonType, string RoleKey,
    decimal Basic, decimal Hra, decimal Allowances, decimal Epf, decimal ProfTax, decimal OtherDeductions);

public sealed record UpsertSalaryStructureRequest(
    string PersonType, string RoleKey,
    decimal Basic, decimal Hra, decimal Allowances, decimal Epf, decimal ProfTax, decimal OtherDeductions);

public sealed record PayrollRunResponse(
    Guid Id, Guid TenantId, string Period, int Year, string? Month, string Status,
    int StaffCount, decimal Gross, decimal Deductions, decimal Net,
    Guid? RunBy, DateTime? RunAt, Guid? ApprovedBy, DateTime? ApprovedAt);

public sealed record PayrollRunLineResponse(
    string PersonType, Guid PersonId, string Name, string? Role, string? Dept,
    decimal Basic, decimal Hra, decimal Allowances, decimal Epf, decimal ProfTax, decimal OtherDeductions,
    decimal Gross, decimal Deductions, decimal Net);

public sealed class PayrollRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<SalaryProfileResponse?> UpsertSalaryProfileAsync(
        Guid tenantId, string personType, Guid personId, UpsertSalaryProfileRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<SalaryProfileResponse>("dbo.SalaryProfile_Upsert", new
        {
            TenantId = tenantId, PersonType = personType, PersonId = personId,
            r.BasicSalary, r.Hra, r.Allowances, r.Epf, r.ProfTax, r.OtherDeductions,
            r.Uan, r.BankHolder, r.BankAccount, r.BankName, r.Ifsc, r.BankBranch,
        }, ct);

    public Task<IReadOnlyList<SalaryProfileResponse>> ListSalaryProfilesAsync(Guid tenantId, CancellationToken ct = default) =>
        QueryProcAsync<SalaryProfileResponse>("dbo.SalaryProfile_List", new { TenantId = tenantId }, ct);

    public Task<SalaryStructureResponse?> UpsertSalaryStructureAsync(
        Guid tenantId, UpsertSalaryStructureRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<SalaryStructureResponse>("dbo.SalaryStructure_Upsert", new
        {
            TenantId = tenantId, r.PersonType, r.RoleKey,
            r.Basic, r.Hra, r.Allowances, r.Epf, r.ProfTax, r.OtherDeductions,
        }, ct);

    public Task<IReadOnlyList<SalaryStructureResponse>> ListSalaryStructuresAsync(Guid tenantId, CancellationToken ct = default) =>
        QueryProcAsync<SalaryStructureResponse>("dbo.SalaryStructure_List", new { TenantId = tenantId }, ct);

    public Task<PayrollRunResponse?> GetRunAsync(Guid tenantId, string period, CancellationToken ct = default) =>
        QuerySingleProcAsync<PayrollRunResponse>("dbo.PayrollRun_Get", new { TenantId = tenantId, Period = period }, ct);

    public Task<IReadOnlyList<PayrollRunResponse>> ListApprovedRunsAsync(Guid tenantId, CancellationToken ct = default) =>
        QueryInlineAsync<PayrollRunResponse>(
            """
            SELECT "Id", "TenantId", "Period", "Year", "Month", "Status", "StaffCount", "Gross", "Deductions", "Net",
                   "RunBy", "RunAt", "ApprovedBy", "ApprovedAt"
            FROM "dbo"."PayrollRuns"
            WHERE "TenantId" = @tenantId AND "Status" IN ('run', 'approved')
            ORDER BY "Period" DESC
            """,
            new { tenantId }, ct);

    public Task<IReadOnlyList<PayrollRunLineResponse>> ListRunLinesAsync(Guid tenantId, string period, CancellationToken ct = default) =>
        QueryProcAsync<PayrollRunLineResponse>("dbo.PayrollRunLine_ListByPeriod", new { TenantId = tenantId, Period = period }, ct);

    public Task<PayrollRunResponse?> SaveRunAsync(
        Guid tenantId, string period, int year, string? month, int staffCount,
        decimal gross, decimal deductions, decimal net, Guid? runBy, string linesJson, CancellationToken ct = default) =>
        QuerySingleProcAsync<PayrollRunResponse>("dbo.PayrollRun_Save", new
        {
            TenantId = tenantId, Period = period, Year = year, Month = month, StaffCount = staffCount,
            Gross = gross, Deductions = deductions, Net = net, RunBy = runBy, Lines = linesJson,
        }, ct);

    public Task<PayrollRunResponse?> ApproveRunAsync(Guid tenantId, string period, Guid? approvedBy, CancellationToken ct = default) =>
        QuerySingleProcAsync<PayrollRunResponse>("dbo.PayrollRun_Approve",
            new { TenantId = tenantId, Period = period, ApprovedBy = approvedBy }, ct);
}

// ---- Fee heads (catalog of fee types) ----
public sealed record FeeHeadResponse(
    Guid Id, Guid TenantId, string Name, string? Code, bool Active, bool IsSystem, bool IsTransportFeeHead,
    string? Description = null);

public sealed record CreateFeeHeadRequest(string Name, string? Code, bool IsTransportFeeHead = false, string? Description = null);
/// <summary>Description follows the same "non-null means set it" convention as Code — sending
/// a non-null Description (including "") updates it; omitting it (null) leaves it untouched.</summary>
public sealed record UpdateFeeHeadRequest(
    string? Name, string? Code, bool? Active, bool? IsTransportFeeHead = null, string? Description = null);

public sealed class FeeHeadRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<IReadOnlyList<FeeHeadResponse>> ListAsync(CancellationToken ct = default) =>
        QueryProcAsync<FeeHeadResponse>("dbo.FeeHead_List", ct: ct);

    public Task<FeeHeadResponse?> CreateAsync(Guid tenantId, CreateFeeHeadRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<FeeHeadResponse>("dbo.FeeHead_Create", new
        {
            TenantId = tenantId,
            Name = r.Name.Trim(),
            Code = string.IsNullOrWhiteSpace(r.Code) ? null : r.Code.Trim(),
            Active = true,
            IsSystem = false,
            r.IsTransportFeeHead,
            Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
        }, ct);

    public Task<FeeHeadResponse?> UpdateAsync(
        Guid id, Guid tenantId, UpdateFeeHeadRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<FeeHeadResponse>("dbo.FeeHead_Update", new
        {
            Id = id,
            TenantId = tenantId,
            Name = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name.Trim(),
            Code = r.Code is null ? null : (string.IsNullOrWhiteSpace(r.Code) ? null : r.Code.Trim()),
            CodeSpecified = r.Code is not null,
            r.Active,
            r.IsTransportFeeHead,
            Description = r.Description is null ? null : (string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim()),
            DescriptionSpecified = r.Description is not null,
        }, ct);

    public async Task<bool> IsTransportFeeHeadAsync(Guid id, Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            """SELECT COUNT(1) FROM "dbo"."FeeHeads" WHERE "Id" = @id AND "TenantId" = @tenantId AND "IsTransportFeeHead" = true""",
            new { id, tenantId }, ct)).First() > 0;

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default)
    {
        var row = await QuerySingleProcAsync<DeleteCountRow>("dbo.FeeHead_Delete",
            new { Id = id, TenantId = tenantId }, ct);
        return row is { Deleted: > 0 };
    }

    private sealed record DeleteCountRow(int Deleted);
}

// ---- Fee structure (named document + class×head amounts JSON) ----
public sealed record FeeStructureRow(
    Guid Id, Guid TenantId, string Name, string AcademicYear, string? ClassGrade, string? Section,
    string Currency, DateTime EffectiveFrom, DateTime? EffectiveTo, string Status,
    string? Description, string AmountsJson, DateTime CreatedAt);

public sealed record FeeStructureResponse(
    Guid? Id, Guid? TenantId, string Name, string AcademicYear,
    [property: JsonPropertyName("class")] string? ClassGrade,
    string? Section, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    string Status, string? Description, JsonElement Amounts);

/// <summary>One saved fee-structure version in the History list — no amounts, kept light.
/// View the full amount breakdown via GET /fees/structure/{id} if/when that's added; today the
/// list exists so an admin can see every version was actually saved, in order.</summary>
public sealed record FeeStructureSummaryResponse(
    Guid Id, string Name, string AcademicYear,
    [property: JsonPropertyName("class")] string? ClassGrade,
    string? Section, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    string Status, string? Description, DateTime CreatedAt, decimal TotalAmount,
    IReadOnlyList<FeeStructureHeadAmountResponse> HeadAmounts);

/// <summary>One fee head's projected revenue for a saved structure version — e.g. "Exam Fee —
/// ₹8,000" — summed (rate × enrolled students) across every class that charges it.</summary>
public sealed record FeeStructureHeadAmountResponse(
    Guid? HeadId, string HeadName, decimal Amount, decimal PerStudentAmount);

public sealed record FeeStructureListRow(
    Guid Id, Guid TenantId, string Name, string AcademicYear, string? ClassGrade, string? Section,
    string Currency, DateTime EffectiveFrom, DateTime? EffectiveTo, string Status,
    string? Description, DateTime CreatedAt, string? AmountsJson);

public sealed record FeeStructurePublishRow(bool Found, Guid? Id);
public sealed record FeeStructureDeleteRow(bool Deleted, string? Reason);
public sealed record FeeStructurePublishResponse(Guid Id, string Status);

public sealed record UpsertFeeStructureRequest(
    Guid? Id,
    string Name,
    string AcademicYear,
    [property: JsonPropertyName("class")] string? ClassGrade,
    string? Section,
    string Currency,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    string? Status,
    string? Description,
    JsonElement? Amounts,
    string? AmountsJson = null);

public sealed class FeeStructureRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<FeeStructureRow?> GetAsync(CancellationToken ct = default) =>
        QuerySingleProcAsync<FeeStructureRow>("dbo.FeeStructure_Get", ct: ct);

    public Task<IReadOnlyList<FeeStructureListRow>> ListHistoryAsync(CancellationToken ct = default) =>
        QueryProcAsync<FeeStructureListRow>("dbo.FeeStructure_List", ct: ct);

    public async Task<FeeStructureRow?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<FeeStructureRow>(
            """
            SELECT "Id", "TenantId", "Name", "AcademicYear", "ClassGrade", "Section", "Currency",
                   "EffectiveFrom", "EffectiveTo", "Status", "Description", "AmountsJson", "CreatedAt"
            FROM "dbo"."FeeStructures" WHERE "Id" = @id
            """,
            new { id }, ct)).FirstOrDefault();

    /// <summary>Every currently-Published (Status = active) version for this tenant — there is
    /// no "only one live row" rule, so this can return more than one. Ordered oldest-first so a
    /// caller merging their amounts together can apply them in order and let the most recently
    /// created version win any (class, head) pair more than one of them sets.</summary>
    public Task<IReadOnlyList<FeeStructureRow>> ListActiveAsync(Guid tenantId, CancellationToken ct = default) =>
        QueryInlineAsync<FeeStructureRow>(
            """
            SELECT "Id", "TenantId", "Name", "AcademicYear", "ClassGrade", "Section", "Currency",
                   "EffectiveFrom", "EffectiveTo", "Status", "Description", "AmountsJson", "CreatedAt"
            FROM "dbo"."FeeStructures" WHERE "TenantId" = @tenantId AND lower("Status") = 'active'
            ORDER BY "CreatedAt" ASC, "Id" ASC
            """,
            new { tenantId }, ct);

    /// <summary>Publishes this version (Status = active). Does not touch any other version's
    /// status — many versions can be Published at the same time.</summary>
    public Task<FeeStructurePublishRow?> PublishAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
        QuerySingleProcAsync<FeeStructurePublishRow>("dbo.FeeStructure_Publish", new { TenantId = tenantId, Id = id }, ct);

    /// <summary>Explicitly retires this version (Status = inactive). Does not touch any other
    /// version's status.</summary>
    public Task<FeeStructurePublishRow?> UnpublishAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
        QuerySingleProcAsync<FeeStructurePublishRow>("dbo.FeeStructure_Unpublish", new { TenantId = tenantId, Id = id }, ct);

    /// <summary>Deletes a draft version outright. Refuses (Deleted = false, Reason = "is_active")
    /// to delete the currently-published version — publish something else first.</summary>
    public Task<FeeStructureDeleteRow?> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
        QuerySingleProcAsync<FeeStructureDeleteRow>("dbo.FeeStructure_Delete", new { TenantId = tenantId, Id = id }, ct);

    public Task<FeeStructureRow?> UpsertAsync(
        Guid tenantId, UpsertFeeStructureRequest r, string amountsJson, CancellationToken ct = default) =>
        QuerySingleProcAsync<FeeStructureRow>("dbo.FeeStructure_Upsert", new
        {
            TenantId = tenantId,
            r.Id,
            Name = r.Name.Trim(),
            AcademicYear = r.AcademicYear.Trim(),
            ClassGrade = string.IsNullOrWhiteSpace(r.ClassGrade) ? null : r.ClassGrade.Trim(),
            Section = string.IsNullOrWhiteSpace(r.Section) ? null : r.Section.Trim(),
            Currency = r.Currency.Trim(),
            EffectiveFrom = r.EffectiveFrom!.Value.ToDateTime(TimeOnly.MinValue),
            EffectiveTo = r.EffectiveTo?.ToDateTime(TimeOnly.MinValue),
            Status = string.IsNullOrWhiteSpace(r.Status) ? "active" : r.Status.Trim().ToLowerInvariant(),
            Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
            AmountsJson = amountsJson,
        }, ct);
}

public static class FinanceModule
{
    public static IServiceCollection AddFinanceModule(this IServiceCollection services)
    {
        services.AddScoped<FeeRepository>();
        services.AddScoped<FeeInvoiceRepository>();
        services.AddScoped<FeeHeadRepository>();
        services.AddScoped<FeeStructureRepository>();
        services.AddScoped<PayslipRepository>();
        services.AddScoped<PayrollRepository>();
        return services;
    }
}
