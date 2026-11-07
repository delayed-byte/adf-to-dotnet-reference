using System.Text;
using System.Text.Json;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;

namespace CreditUnion.Batch.UnitTests.Master;

/// <summary>
/// The master's ops.BatchRun events and the Failed-event builder (ADR 0007, ADR 0014), and the evidence file format and path
/// (ADR 0015). The expressions themselves are read from the ADF JSON in <see cref="MasterPolicyTests"/>.
/// </summary>
public sealed class MasterRunEventsTests
{
    private static readonly Guid RunId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly string Pfe = typeof(PipelineFailedException).FullName!;
    private static readonly string Tfe = "Microsoft.DurableTask.TaskFailedException";

    [Fact]
    public void A_child_that_failed_with_an_ADF_error_code_is_recorded_with_that_code_and_its_detail()
    {
        var failure = StepFailure.From(PipelineNames.MapAndAggregate, [new(Pfe, "TXN_CODE_UNMAPPED: Unmapped transaction codes: P2PD")]);

        failure.ShouldBe(new StepFailure(PipelineNames.MapAndAggregate, PipelineErrorCodes.TxnCodeUnmapped, "Unmapped transaction codes: P2PD"));
        failure.FailMessage.ShouldBe("PL_MapAndAggregate failed: Unmapped transaction codes: P2PD", "Fail At Map: concat('PL_MapAndAggregate failed: ', error.message)");
        new PipelineFailedException(PipelineErrorCodes.BatchStepFailed, failure.FailMessage).Message
            .ShouldBe("BATCH_STEP_FAILED: PL_MapAndAggregate failed: Unmapped transaction codes: P2PD");
    }

    [Fact]
    public void A_child_that_failed_in_an_activity_is_recorded_with_the_root_cause_type_and_the_childs_message()
    {
        var chain = new FailureLink[]
        {
            new(Tfe, "Task 'Extract_CopyToStaging' (#4) failed with an unhandled exception: Extract_CopyToStaging exceeded its ADF timeout of 01:00:00."),
            new("System.TimeoutException", "Extract_CopyToStaging exceeded its ADF timeout of 01:00:00."),
        };

        var failure = StepFailure.From(PipelineNames.Extract, chain);

        failure.ErrorCode.ShouldBe("TimeoutException", "no ADF code exists, so the root cause's type names the failure");
        failure.ErrorMessage.ShouldBe(chain[0].ErrorMessage, "ADF: activity('Execute Extract KeyStone').error.message, the child's failure as reported");
        failure.FailMessage.ShouldStartWith("PL_ExtractKeyStone failed: Task 'Extract_CopyToStaging'");
    }

    [Fact]
    public void A_code_deeper_in_the_chain_is_still_the_childs_code()
    {
        var failure = StepFailure.From(PipelineNames.Reconciliation,
        [
            new(Tfe, "Task 'X' (#1) failed with an unhandled exception: SOME_CODE: detail"),
            new(Pfe, "SOME_CODE: detail"),
        ]);

        (failure.ErrorCode, failure.ErrorMessage).ShouldBe(("SOME_CODE", "Task 'X' (#1) failed with an unhandled exception: SOME_CODE: detail"));
    }

    [Fact]
    public void The_evidence_step_uses_its_own_step_name_and_Fail_message()
    {
        var failure = StepFailure.From(MasterSteps.ExportRunEvidence, [new(Tfe, "Task 'Master_ExportRunEvidence' (#7) failed"), new("Azure.RequestFailedException", "Service unavailable")]);

        failure.StepName.ShouldBe("ExportRunEvidence");
        failure.ErrorCode.ShouldBe("RequestFailedException");
        failure.FailMessage.ShouldBe("Run evidence export failed: Task 'Master_ExportRunEvidence' (#7) failed");
    }

    [Fact]
    public void Codes_and_messages_are_cut_to_the_procedure_parameter_sizes()
    {
        var failure = StepFailure.From(PipelineNames.Extract, [new(Tfe, new string('m', 5000)), new("Ns." + new string('T', 80), "x")]);

        failure.ErrorMessage.Length.ShouldBe(StepFailure.MaxErrorMessageLength);
        failure.ErrorCode.ShouldBe(new string('T', StepFailure.MaxErrorCodeLength));
        StepFailure.ShortTypeName("A.B.Outer+Inner").ShouldBe("Inner");
        StepFailure.ShortTypeName("Plain").ShouldBe("Plain");
        Should.Throw<ArgumentException>(() => StepFailure.From(PipelineNames.Extract, []));
    }

    [Fact]
    public void The_Started_Succeeded_and_Failed_events_carry_the_ADF_parameters()
    {
        var started = new DateTime(2026, 11, 5, 11, 0, 1, DateTimeKind.Utc);
        var trigger = new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);

        MasterRunEvents.Started(RunId, Date, "TR_Nightly_0300_Pacific", trigger, started).ShouldBe(new LogBatchRunEventRequest(RunId, Date, PipelineNames.Master, "Started",
            Notes: "trigger=TR_Nightly_0300_Pacific; triggerTimeUtc=2026-11-05T11:00:00.0000000Z", StartedUtc: started));
        MasterRunEvents.StartedNotes(MasterRunEvents.ManualTriggerName, new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.FromHours(-8)))
            .ShouldBe("trigger=Manual; triggerTimeUtc=2026-11-05T11:00:00.0000000Z", "always UTC");
        MasterRunEvents.Succeeded(RunId, Date, "evidence/2026-11-04/batch-run-x.json", started).ShouldBe(new LogBatchRunEventRequest(RunId, Date, PipelineNames.Master, "Succeeded",
            ObjectName: "evidence/2026-11-04/batch-run-x.json", EndedUtc: started));
        MasterRunEvents.Failed(RunId, Date, new StepFailure("PL_MapAndAggregate", "TXN_CODE_UNMAPPED", "Unmapped transaction codes: P2PD"), started).ShouldBe(
            new LogBatchRunEventRequest(RunId, Date, PipelineNames.Master, "Failed", StepName: "PL_MapAndAggregate", ErrorCode: "TXN_CODE_UNMAPPED",
                ErrorMessage: "Unmapped transaction codes: P2PD", EndedUtc: started));
    }

    [Fact]
    public void The_evidence_path_and_object_name_follow_the_dataset_and_the_Succeeded_expression()
    {
        RunEvidenceFile.BlobName(Date, RunId).ShouldBe("2026-11-04/batch-run-0f8fad5b-d9cb-469f-a165-70867728950e.json", "lowercase hyphenated, as pipeline().RunId prints");
        RunEvidenceFile.ObjectName("evidence", Date, RunId).ShouldBe("evidence/2026-11-04/batch-run-0f8fad5b-d9cb-469f-a165-70867728950e.json");
        RunEvidenceFile.Metadata("TBD").ShouldBe(new Dictionary<string, string> { ["retain_until"] = "TBD" });
        RunEvidenceFile.Metadata("2033-12-31").ShouldBe(new Dictionary<string, string> { ["retain_until"] = "2033-12-31" });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" TBD")]
    [InlineData("TBD ")]
    [InlineData("café")]
    [InlineData("a\nb")]
    public void A_retainUntil_that_cannot_be_blob_metadata_is_refused_before_any_IO(string value)
    {
        Should.Throw<RunEvidenceInputException>(() => RunEvidenceFile.Metadata(value)).Message.ShouldNotContain(value.Length > 0 ? value : "\u0000");
    }

    [Fact]
    public async Task The_evidence_is_a_JSON_array_of_section_item_value_objects_in_the_procedure_order_UTF8_without_BOM()
    {
        using var stream = new MemoryStream();
        var count = await RunEvidenceFile.WriteJsonAsync(Rows(
            new("Run", "batchRunId", RunId.ToString()),
            new("Files", "settlement_001.csv", "Archived; detailRows=619; trailerRows=619; errors=-"),
            new("Exceptions", "GL/1010:DEBIT", null),
            new("Files", "résumé \"q\" \\.csv", "<tag> & 'x'")), stream, CancellationToken.None);

        count.ShouldBe(4);
        var bytes = stream.ToArray();
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF }, "no byte-order mark");
        var text = Encoding.UTF8.GetString(bytes);
        text.ShouldContain("résumé", Case.Sensitive, "non-ASCII is written as is");
        text.ShouldContain("<tag> & 'x'", Case.Sensitive, "no HTML escaping");
        using var document = JsonDocument.Parse(bytes);
        var items = document.RootElement.EnumerateArray().ToList();
        items.Select(i => string.Join(",", i.EnumerateObject().Select(p => p.Name))).Distinct().ShouldBe(["section,item,value"], "every object has the three columns in order");
        items.Select(i => i.GetProperty("section").GetString()).ShouldBe(["Run", "Files", "Exceptions", "Files"], "the procedure's order");
        items[2].GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        items[3].GetProperty("item").GetString().ShouldBe("résumé \"q\" \\.csv");
    }

    [Fact]
    public async Task An_empty_result_set_is_an_empty_array()
    {
        using var stream = new MemoryStream();

        (await RunEvidenceFile.WriteJsonAsync(Rows(), stream, CancellationToken.None)).ShouldBe(0);

        JsonDocument.Parse(stream.ToArray()).RootElement.GetArrayLength().ShouldBe(0);
    }

    private static async IAsyncEnumerable<RunEvidenceEntry> Rows(params RunEvidenceEntry[] rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }
}
