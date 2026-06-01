using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <see cref="WaveFunction"/> の設定境界と entropy 選択ロジックを固定する EditMode test。
/// </summary>
/// <remarks>
/// Unity object を必要とする初期化境界は小さい GameObject を作って検証し、
/// 純粋な候補選択ロジックは managed 配列から test helper を通して検証します。
///
/// 目的:
/// - Inspector 設定ミスを実行前に止めること。
/// - neighbor 参照のプロトコル違反を黙って無視しないこと。
/// - 同 entropy の tie-break と contradiction 検出を再発防止すること。
/// </remarks>
public sealed class WaveFunctionTests
{
    /// <summary>
    /// dimensions が 0 の設定を実行前エラーとして扱うことを検証する。
    /// </summary>
    /// <remarks>
    /// <see cref="WaveFunction"/> は <c>dimensions * dimensions</c> の配列を確保するため、
    /// 0 以下を許すと空生成や後続 index error の原因になります。
    /// </remarks>
    [Test]
    public void InvalidDimensionsDisableGeneration()
    {
        Tile tile = CreateTile("tile");
        Cell cell = CreateCellPrefab();
        GameObject owner = new GameObject("wave-test");
        owner.SetActive(false);

        WaveFunction wave = owner.AddComponent<WaveFunction>();
        wave.dimensions = 0;
        wave.tileObjects = new[] { tile };
        wave.cellObj = cell;

        owner.SetActive(true);

        Assert.IsFalse(wave.enabled);

        Object.DestroyImmediate(owner);
        Object.DestroyImmediate(tile.gameObject);
        Object.DestroyImmediate(cell.gameObject);
    }

    /// <summary>
    /// neighbor 配列が <see cref="WaveFunction.tileObjects"/> 外の tile を参照した場合に生成を止めることを検証する。
    /// </summary>
    /// <remarks>
    /// 未登録 tile を黙って無視すると mask が欠け、WFC の制約が意図せず強くなります。
    /// このテストは「設定ミスは初期化時に明示エラー」という境界を固定します。
    /// </remarks>
    [Test]
    public void UnknownNeighbourReferenceDisablesGeneration()
    {
        Tile known = CreateTile("known");
        Tile unknown = CreateTile("unknown");
        known.upNeighbours = new[] { unknown };

        GameObject owner = new GameObject("wave-test");
        owner.SetActive(false);

        WaveFunction wave = owner.AddComponent<WaveFunction>();
        wave.dimensions = 1;
        wave.tileObjects = new[] { known };
        Cell cell = CreateCellPrefab();
        wave.cellObj = cell;

        owner.SetActive(true);

        Assert.IsFalse(wave.enabled);

        Object.DestroyImmediate(owner);
        Object.DestroyImmediate(known.gameObject);
        Object.DestroyImmediate(unknown.gameObject);
        Object.DestroyImmediate(cell.gameObject);
    }

    /// <summary>
    /// 同じ entropy のセルが複数ある場合、seed によって選択先が変わることを検証する。
    /// </summary>
    /// <remarks>
    /// 固定順で常に先頭セルを選ぶと、生成結果がグリッド index に偏ります。
    /// このテストは tie-break 経路が存在することを確認します。
    /// </remarks>
    [Test]
    public void LowestEntropyUsesTieBreakSeed()
    {
        ulong[] options = { 0b0011UL, 0b0101UL, 0b1111UL };
        byte[] collapsed = { 0, 0, 0 };

        int first = WaveFunction.FindLowestEntropyCellForTest(options, collapsed, 0);
        int second = WaveFunction.FindLowestEntropyCellForTest(options, collapsed, 1);

        Assert.AreEqual(0, first);
        Assert.AreEqual(1, second);
    }

    /// <summary>
    /// 候補数 0 の未 collapse セルを通常の低 entropy セルより先に報告することを検証する。
    /// </summary>
    /// <remarks>
    /// 候補数 0 は「制約の結果、置ける tile がない」という contradiction です。
    /// 通常の collapse 対象として扱わず、生成停止と診断に進める必要があります。
    /// </remarks>
    [Test]
    public void ContradictionCellIsReportedBeforeNormalEntropyCell()
    {
        ulong[] options = { 0b0011UL, 0UL, 0b0001UL };
        byte[] collapsed = { 0, 0, 0 };

        int contradiction = WaveFunction.FindLowestEntropyCellForTest(options, collapsed, 0);

        Assert.AreEqual(1, contradiction);
    }

    /// <summary>
    /// テスト用 tile GameObject を作る。
    /// </summary>
    /// <param name="name">
    /// GameObject 名。
    /// </param>
    /// <returns>
    /// 全方向の neighbor 配列が空で初期化された <see cref="Tile"/>。
    /// </returns>
    /// <remarks>
    /// Caller:
    /// - 戻り値の GameObject は caller が <see cref="Object.DestroyImmediate(Object)"/> で破棄します。
    /// </remarks>
    private static Tile CreateTile(string name)
    {
        GameObject go = new GameObject(name);
        Tile tile = go.AddComponent<Tile>();
        tile.upNeighbours = new Tile[0];
        tile.rightNeighbours = new Tile[0];
        tile.downNeighbours = new Tile[0];
        tile.leftNeighbours = new Tile[0];
        return tile;
    }

    /// <summary>
    /// テスト用 Cell prefab 相当の GameObject を作る。
    /// </summary>
    /// <returns>
    /// 新しく作成した GameObject に追加された <see cref="Cell"/>。
    /// </returns>
    /// <remarks>
    /// Caller:
    /// - 戻り値の GameObject は caller が破棄します。
    /// - 実 prefab asset は作らず、EditMode test 内の一時 GameObject だけを使います。
    /// </remarks>
    private static Cell CreateCellPrefab()
    {
        GameObject go = new GameObject("cell");
        return go.AddComponent<Cell>();
    }
}
