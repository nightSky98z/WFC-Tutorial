using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <see cref="WaveFunction"/> の重要な境界を確認する EditMode test。
/// </summary>
/// <remarks>
/// 設定ミス、entropy 選択、contradiction 検出を固定します。
/// </remarks>
public sealed class WaveFunctionTests
{
    /// <summary>
    /// dimensions が 0 なら生成を止める。
    /// </summary>
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
    /// 未登録 tile を neighbor に入れたら生成を止める。
    /// </summary>
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
    /// 同じ entropy のセルは seed で選択先を変える。
    /// </summary>
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
    /// 候補 0 のセルを contradiction として先に返す。
    /// </summary>
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
    /// neighbor 配列が空の <see cref="Tile"/>。
    /// </returns>
    /// <remarks>
    /// 呼び出し側が GameObject を破棄します。
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
    /// 新しい GameObject 上の <see cref="Cell"/>。
    /// </returns>
    /// <remarks>
    /// 実 prefab asset は作らず、一時 GameObject だけを使います。
    /// </remarks>
    private static Cell CreateCellPrefab()
    {
        GameObject go = new GameObject("cell");
        return go.AddComponent<Cell>();
    }
}
