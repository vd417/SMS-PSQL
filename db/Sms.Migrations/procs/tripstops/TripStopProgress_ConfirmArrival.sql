CREATE OR ALTER PROCEDURE dbo.TripStopProgress_ConfirmArrival
    @TenantId uniqueidentifier, @TripId uniqueidentifier, @StopId uniqueidentifier,
    @Seq int, @ArrivedAt datetime2, @ConfirmedAt datetime2
AS
BEGIN
    SET NOCOUNT ON;
    -- Atomically claim this stop as the trip's current stop. Only the caller that still sees a NULL
    -- CurrentStopId wins (returns 1); a concurrent/duplicate confirm changes nothing (returns 0), so
    -- the caller can suppress a duplicate arrival broadcast. Gating the MERGE on that win also stops
    -- a losing confirm from resetting ConfirmedAt.
    UPDATE dbo.Trips SET CurrentStopId = @StopId
    WHERE Id = @TripId AND TenantId = @TenantId AND CurrentStopId IS NULL;
    IF @@ROWCOUNT = 0 RETURN 0;

    MERGE dbo.TripStopProgress AS tgt
    USING (SELECT @TripId AS TripId, @StopId AS StopId) AS src
        ON tgt.TenantId = @TenantId AND tgt.TripId = src.TripId AND tgt.StopId = src.StopId
    WHEN MATCHED THEN
        UPDATE SET ArrivedAt = ISNULL(tgt.ArrivedAt, @ArrivedAt), ConfirmedAt = @ConfirmedAt
    WHEN NOT MATCHED THEN
        INSERT (Id, TenantId, TripId, StopId, Seq, ArrivedAt, ConfirmedAt)
        VALUES (NEWID(), @TenantId, @TripId, @StopId, @Seq, @ArrivedAt, @ConfirmedAt);

    RETURN 1;
END
