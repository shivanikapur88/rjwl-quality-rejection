using System.Text.Json;

namespace Quality.Tests.Integration;

/// <summary>Brief §5/§16B: the 145 seeded phenomena in a fresh database equal the workbook-derived seed EXACTLY (not just the count).</summary>
public sealed class ReferenceDataTests(SqlFixture f) : SqlTest(f)
{
    private sealed record SeedRow(string StageCode, string Cell, string Original, string Normalized, string? Code, string Status, string? DuplicateOf);

    private static List<SeedRow> Seed()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(SqlFixture.DbDir, "seed", "phenomena_seed.json")));
        return doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => new SeedRow(
            r.GetProperty("stageCode").GetString()!, r.GetProperty("cell").GetString()!, r.GetProperty("original").GetString()!,
            r.GetProperty("normalized").GetString()!, r.GetProperty("code").GetString(), r.GetProperty("status").GetString()!,
            r.GetProperty("duplicateOf").GetString())).ToList();
    }

    [Fact]
    public async Task Database_phenomena_match_the_seed_exactly()
    {
        var expected = Seed().Where(r => r.Status == "IMPORTED")
            .Select(r => $"{r.StageCode}|{r.Code}|{r.Original}|{r.Normalized}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var actual = (await SqlFixture.Rows(F.MasterCs, """
            SELECT s.StageCode, p.PhenomenonCode, p.PhenomenonName, p.NormalizedName FROM qa.RejectionPhenomena p
            JOIN qa.InspectionStages s ON s.Id = p.InspectionStageId WHERE p.CreatedBy IS NULL
            """)).Select(r => $"{r[0]}|{r[1]}|{r[2]}|{r[3]}").OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(145, expected.Count);
        Assert.Equal(expected, actual);   // every code, original text (incl. double spaces) and normalized key
    }

    [Fact]
    public void Seed_counts_and_documented_duplicate()
    {
        var rows = Seed();
        Assert.Equal(146, rows.Count);
        Assert.Equal(new[] { 25, 52, 34, 34 }, new[] { "GDC_SHOP", "CNC_SHOP", "PRE_TREATMENT", "FINAL" }.Select(s => rows.Count(r => r.StageCode == s && r.Status == "IMPORTED")));
        var skipped = Assert.Single(rows, r => r.Status != "IMPORTED");
        Assert.Equal(("CNC_SHOP", "B42", "DENT", "B8"), (skipped.StageCode, skipped.Cell, skipped.Original, skipped.DuplicateOf));
        Assert.Contains(rows, r => r.Original == "LUG HEIGHT  O/S /UNCLEAN" && r.Normalized == "LUG HEIGHT O/S /UNCLEAN");
    }

    [Fact]
    public async Task Pre_and_final_are_independent_stages_with_their_own_rows()
    {
        var rows = await SqlFixture.Rows(F.MasterCs, """
            SELECT sh.ShopCode, st.StageCode, COUNT(p.Id) FROM qa.Shops sh
            JOIN qa.ShopStages ss ON ss.ShopId = sh.Id JOIN qa.InspectionStages st ON st.Id = ss.InspectionStageId
            LEFT JOIN qa.RejectionPhenomena p ON p.InspectionStageId = st.Id AND p.CreatedBy IS NULL
            GROUP BY sh.ShopCode, st.StageCode ORDER BY sh.ShopCode
            """);
        Assert.Equal(new[] { "CNC|CNC_SHOP|52", "FINAL|FINAL|34", "GDC|GDC_SHOP|25", "PRE|PRE_TREATMENT|34" }, rows.Select(r => $"{r[0]}|{r[1]}|{r[2]}"));
        // Same names, different rows: renaming one never renames the other.
        Assert.Equal(0, await SqlFixture.Scalar<int>(F.MasterCs, """
            SELECT COUNT(*) FROM qa.RejectionPhenomena a JOIN qa.RejectionPhenomena b ON a.Id = b.Id
            JOIN qa.InspectionStages sa ON sa.Id = a.InspectionStageId JOIN qa.InspectionStages sb ON sb.Id = b.InspectionStageId
            WHERE sa.StageCode = 'PRE_TREATMENT' AND sb.StageCode = 'FINAL'
            """));
    }

    [Fact]
    public async Task Rerunning_the_seed_after_an_admin_added_a_phenomenon_succeeds_and_inserts_nothing()
    {
        // An administrator-added phenomenon (the old script's "count = 25" check failed on a re-run once one existed).
        await SqlFixture.Scalar<int>(F.MasterCs, """
            INSERT INTO qa.RejectionPhenomena (PhenomenonCode, PhenomenonName, NormalizedName, InspectionStageId, IsActive, CreatedBy)
            SELECT 'GDC_SHOP-RERUN', 'Rerun check', 'RERUN CHECK', s.Id, 1, (SELECT MIN(Id) FROM qa.Users) FROM qa.InspectionStages s WHERE s.StageCode = 'GDC_SHOP';
            SELECT 1
            """);
        const string seeded = "SELECT COUNT(*) FROM qa.RejectionPhenomena WHERE CreatedBy IS NULL";
        var before = await SqlFixture.Scalar<int>(F.MasterCs, seeded);
        await SqlFixture.RunScripts(F.MasterCs, "04_seed_phenomena", "03_seed_reference", "08_upgrade_master_review_fixes");
        Assert.Equal(145, before);
        Assert.Equal(before, await SqlFixture.Scalar<int>(F.MasterCs, seeded));
    }
}
