-- Appends one event to the ops.BatchRun audit trail. Empty strings are stored as NULL
-- (ADF passes '' for unset optional parameters).
CREATE OR ALTER PROCEDURE ops.usp_LogBatchRunEvent
    @BatchRunId    uniqueidentifier,
    @BusinessDate  date,
    @PipelineName  varchar(100),
    @Status        varchar(30),
    @StepName      varchar(100)   = NULL,
    @ObjectName    nvarchar(400)  = NULL,
    @RecordCount   bigint         = NULL,
    @ErrorCode     varchar(50)    = NULL,
    @ErrorMessage  nvarchar(2000) = NULL,
    @Notes         nvarchar(2000) = NULL,
    @StartedUtc    datetime2(3)   = NULL,
    @EndedUtc      datetime2(3)   = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO ops.BatchRun
        (BatchRunId, BusinessDate, PipelineName, StepName, Status, ObjectName, RecordCount,
         ErrorCode, ErrorMessage, Notes, StartedUtc, EndedUtc)
    VALUES
        (@BatchRunId, @BusinessDate, @PipelineName, NULLIF(@StepName, ''), @Status, NULLIF(@ObjectName, N''), @RecordCount,
         NULLIF(@ErrorCode, ''), NULLIF(@ErrorMessage, N''), NULLIF(@Notes, N''), @StartedUtc, @EndedUtc);
END;
GO
