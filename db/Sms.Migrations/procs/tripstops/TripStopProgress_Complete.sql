CREATE OR ALTER PROCEDURE dbo.TripStopProgress_Complete
    @TenantId uniqueidentifier, @TripId uniqueidentifier, @StopId uniqueidentifier, @DepartedAt datetime2
AS
BEGIN
    SET NOCOUNT ON;
    -- Atomically release the current stop. Only the caller that still sees CurrentStopId = @StopId
    -- wins (returns 1); a concurrent/duplicate depart changes nothing (returns 0), so the caller can
    -- suppress a duplicate completion broadcast. Gating the DepartedAt write on that win keeps the
    -- two updates consistent.
    UPDATE dbo.Trips SET CurrentStopId = NULL
    WHERE Id = @TripId AND TenantId = @TenantId AND CurrentStopId = @StopId;
    IF @@ROWCOUNT = 0 RETURN 0;

    UPDATE dbo.TripStopProgress SET DepartedAt = @DepartedAt
    WHERE TenantId = @TenantId AND TripId = @TripId AND StopId = @StopId;

    RETURN 1;
END
