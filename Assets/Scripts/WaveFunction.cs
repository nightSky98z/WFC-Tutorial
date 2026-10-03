using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;

/// <summary>
/// WFC で 2D マップを生成する MonoBehaviour。
/// </summary>
/// <remarks>
/// 読む順番:
/// 1. <see cref="Tile"/> の接続ルールを bit mask に変換する。
/// 2. entropy が低いセルを 1 つ collapse する。
/// 3. <see cref="UpdateGenerationJob"/> で候補を全セルへ伝播する。
///
/// Unity object は main thread、候補計算は <see cref="NativeArray{T}"/> と Job が担当します。
/// </remarks>
public class WaveFunction : MonoBehaviour
{
    /// <summary>
    /// <c>ulong</c> 1 個で扱える tile 数。
    /// </summary>
    /// <remarks>
    /// 1 tile = 1 bit なので最大 64 種類です。
    /// </remarks>
    private const int MaxTileCount = 64;

    // Inspector から設定する値。

    /// <summary>
    /// グリッド一辺のセル数。
    /// </summary>
    /// <remarks>
    /// 総セル数は <c>dimensions * dimensions</c> です。
    /// </remarks>
    public int dimensions;

    /// <summary>
    /// WFC が配置する tile prefab。
    /// </summary>
    /// <remarks>
    /// 配列 index が tile id です。
    /// </remarks>
    public Tile[] tileObjects;

    /// <summary>
    /// Scene に生成した Cell marker。
    /// </summary>
    /// <remarks>
    /// 計算本体ではなく、位置と状態を見やすくする表示用です。
    /// </remarks>
    public List<Cell> gridComponents;

    /// <summary>
    /// Cell marker の prefab。
    /// </summary>
    public Cell cellObj;

    /// <summary>
    /// 隣り合うセル中心の距離。
    /// </summary>
    /// <remarks>
    /// Sprite の Pixels Per Unit と合わせます。
    /// </remarks>
    [SerializeField] private float cellSize = 1.0f;

    /// <summary>
    /// マップ中心を合わせる Camera。
    /// </summary>
    /// <remarks>
    /// null の場合は、この GameObject の位置を中心にします。
    /// </remarks>
    [SerializeField] private Camera targetCamera;

    // 実行中に変化する状態。

    /// <summary>
    /// collapse と伝播を進めた回数。
    /// </summary>
    int iterations = 0;

    /// <summary>
    /// 現在の各セル候補。
    /// </summary>
    /// <remarks>
    /// bit が 1 の tile が候補です。
    /// </remarks>
    private NativeArray<ulong> currentOptions;

    /// <summary>
    /// Job が書き込む次の候補。
    /// </summary>
    private NativeArray<ulong> nextOptions;

    /// <summary>
    /// 各セルが collapse 済みかどうか。
    /// </summary>
    /// <remarks>
    /// 0 は未決定、1 は決定済みです。
    /// </remarks>
    private NativeArray<byte> collapsed;

    /// <summary>
    /// collapse 後に選ばれた tile id。
    /// </summary>
    /// <remarks>
    /// 未決定セルは -1 です。
    /// </remarks>
    private NativeArray<int> collapsedTileIndices;

    // Tile の接続ルールを bit mask にしたもの。

    /// <summary>
    /// tile ごとの「上に置ける tile」mask。
    /// </summary>
    private NativeArray<ulong> upMasks;

    /// <summary>
    /// tile ごとの「右に置ける tile」mask。
    /// </summary>
    private NativeArray<ulong> rightMasks;

    /// <summary>
    /// tile ごとの「下に置ける tile」mask。
    /// </summary>
    private NativeArray<ulong> downMasks;

    /// <summary>
    /// tile ごとの「左に置ける tile」mask。
    /// </summary>
    private NativeArray<ulong> leftMasks;

    /// <summary>
    /// 全 tile が候補の mask。
    /// </summary>
    private ulong allOptions;

    /// <summary>
    /// Unity 起動時に WFC の準備をする。
    /// </summary>
    /// <remarks>
    /// 設定確認、mask 作成、配列確保、Cell 生成を順に行います。
    /// 設定ミスがあればこの component を止めます。
    /// </remarks>
    void Awake()
    {
        if (!TryValidateConfig())
        {
            enabled = false;
            return;
        }

        int cellCount = dimensions * dimensions;

        allOptions = BuildAllOptionsMask(tileObjects.Length);

        if (!TryBuildTileMasks())
        {
            DisposeNativeArrays();
            enabled = false;
            return;
        }

        currentOptions = new NativeArray<ulong>(cellCount, Allocator.Persistent);
        nextOptions = new NativeArray<ulong>(cellCount, Allocator.Persistent);
        collapsed = new NativeArray<byte>(cellCount, Allocator.Persistent);
        collapsedTileIndices = new NativeArray<int>(cellCount, Allocator.Persistent);


        for (int i = 0; i < cellCount; i++)
        {
            currentOptions[i] = allOptions;
            nextOptions[i] = allOptions;
            collapsed[i] = 0;
            collapsedTileIndices[i] = -1;
        }

        gridComponents = new List<Cell>(cellCount);
        InitializeGrid();
    }

    /// <summary>
    /// Inspector 設定を確認する。
    /// </summary>
    /// <returns>
    /// 実行できる設定なら true。
    /// </returns>
    /// <remarks>
    /// 配列確保や bit shift の前に、危ない値を止めます。
    /// </remarks>
    bool TryValidateConfig()
    {
        if (dimensions <= 0)
        {
            Debug.LogError("WFC configuration error: dimensions must be greater than 0.");
            return false;
        }

        long cellCount = (long)dimensions * dimensions;
        if (cellCount > int.MaxValue)
        {
            Debug.LogError("WFC configuration error: dimensions * dimensions exceeds int.MaxValue.");
            return false;
        }

        if (cellSize <= 0.0f)
        {
            Debug.LogError("WFC configuration error: cellSize must be greater than 0.");
            return false;
        }

        if (cellObj == null)
        {
            Debug.LogError("WFC configuration error: cellObj is not assigned.");
            return false;
        }

        if (tileObjects == null || tileObjects.Length == 0 || tileObjects.Length > MaxTileCount)
        {
            Debug.LogError($"WFC configuration error: tileObjects length must be 1..{MaxTileCount}.");
            return false;
        }

        for (int i = 0; i < tileObjects.Length; i++)
        {
            if (tileObjects[i] == null)
            {
                Debug.LogError($"WFC configuration error: tileObjects[{i}] is null.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 「全 tile が候補」の mask を作る。
    /// </summary>
    /// <param name="tileCount">
    /// tile 種類数。
    /// </param>
    /// <returns>
    /// index 0 から <paramref name="tileCount"/> - 1 までの bit が立った mask。
    /// </returns>
    /// <remarks>
    /// 例: tileCount が 4 なら <c>0b1111</c> です。
    /// </remarks>
    static ulong BuildAllOptionsMask(int tileCount)
    {
        return tileCount == MaxTileCount
            ? ulong.MaxValue
            : (1UL << tileCount) - 1UL;
    }

    /// <summary>
    /// 全 tile の接続ルールを bit mask に変換する。
    /// </summary>
    /// <returns>
    /// 変換できたら true。
    /// </returns>
    /// <remarks>
    /// null や未登録 tile は設定エラーとして止めます。
    /// </remarks>
    bool TryBuildTileMasks()
    {
        int tileCount = tileObjects.Length;

        upMasks = new NativeArray<ulong>(tileCount, Allocator.Persistent);
        rightMasks = new NativeArray<ulong>(tileCount, Allocator.Persistent);
        downMasks = new NativeArray<ulong>(tileCount, Allocator.Persistent);
        leftMasks = new NativeArray<ulong>(tileCount, Allocator.Persistent);

        for (int i = 0; i < tileCount; i++)
        {
            Tile tile = tileObjects[i];
            if (!TryBuildMask(tile.upNeighbours, tile, "up", out ulong upMask))
            {
                return false;
            }
            upMasks[i] = upMask;

            if (!TryBuildMask(tile.rightNeighbours, tile, "right", out ulong rightMask))
            {
                return false;
            }
            rightMasks[i] = rightMask;

            if (!TryBuildMask(tile.downNeighbours, tile, "down", out ulong downMask))
            {
                return false;
            }
            downMasks[i] = downMask;

            if (!TryBuildMask(tile.leftNeighbours, tile, "left", out ulong leftMask))
            {
                return false;
            }
            leftMasks[i] = leftMask;
        }

        return true;
    }

    /// <summary>
    /// 1 方向分の tile 配列を mask に変換する。
    /// </summary>
    /// <param name="tiles">
    /// 変換する neighbor 配列。
    /// </param>
    /// <param name="owner">
    /// エラー表示用の tile。
    /// </param>
    /// <param name="directionName">
    /// エラー表示用の方向名。
    /// </param>
    /// <param name="mask">
    /// 成功時に結果の mask を書き込みます。
    /// </param>
    /// <returns>
    /// 変換できたら true。
    /// </returns>
    /// <remarks>
    /// 例: tile id 0 と 2 が入っていれば <c>0b0101</c> です。
    /// </remarks>
    bool TryBuildMask(Tile[] tiles, Tile owner, string directionName, out ulong mask)
    {
        mask = 0UL;

        if (tiles == null)
        {
            Debug.LogError($"WFC configuration error: {owner.name}.{directionName}Neighbours is null. Use an empty array for no neighbours.");
            return false;
        }

        for (int i = 0; i < tiles.Length; ++i)
        {
            if (tiles[i] == null)
            {
                Debug.LogError($"WFC configuration error: {owner.name}.{directionName}Neighbours[{i}] is null.");
                return false;
            }

            int tileIndex = Array.IndexOf(tileObjects, tiles[i]);
            if (tileIndex < 0)
            {
                Debug.LogError($"WFC configuration error: {owner.name}.{directionName}Neighbours[{i}] references {tiles[i].name}, but it is not in tileObjects.");
                return false;
            }

            mask |= 1UL << tileIndex;
        }

        return true;
    }

    /// <summary>
    /// Cell marker を並べて、最初の WFC step を始める。
    /// </summary>
    /// <remarks>
    /// マップ全体の中心が Camera またはこの GameObject の位置に来るように配置します。
    /// </remarks>
    void InitializeGrid()
    {
        Vector2 center = targetCamera != null
            ? (Vector2)targetCamera.transform.position
            : (Vector2)transform.position;

        float halfMapSize = (dimensions - 1) * cellSize * 0.5f;
        Vector2 origin = center - new Vector2(halfMapSize, halfMapSize);
        for (int y = 0; y < dimensions; y++)
        {
            for (int x = 0; x < dimensions; x++)
            {
                Vector2 position = origin + new Vector2(x * cellSize, y * cellSize);
                Cell newCell = Instantiate(cellObj, position, Quaternion.identity);
                newCell.CreateCell(false, Array.Empty<Tile>());
                gridComponents.Add(newCell);
            }
        }

        StartCoroutine(CheckEntropy());
    }

    /// <summary>
    /// 次に collapse するセルを探す。
    /// </summary>
    /// <param name="foundContradiction">
    /// 候補 0 のセルが見つかったら true。
    /// </param>
    /// <returns>
    /// セル index。全セルが決定済みなら -1。
    /// </returns>
    /// <remarks>
    /// entropy は候補 tile 数です。少ないほど先に決めます。
    /// </remarks>
    int FindLowestEntropyCell(out bool foundContradiction)
    {
        int tieBreakSeed = UnityEngine.Random.Range(0, int.MaxValue);
        return FindLowestEntropyCell(currentOptions, collapsed, tieBreakSeed, out foundContradiction);
    }

    /// <summary>
    /// 候補 mask から最小 entropy のセルを選ぶ。
    /// </summary>
    /// <param name="optionMasks">
    /// 各セルの候補 mask。
    /// </param>
    /// <param name="collapsedFlags">
    /// 各セルの collapse 済み flag。
    /// </param>
    /// <param name="tieBreakSeed">
    /// 同じ entropy のセルから選ぶための seed。
    /// </param>
    /// <param name="foundContradiction">
    /// 候補 0 のセルが見つかったら true。
    /// </param>
    /// <returns>
    /// 選ばれたセル index。なければ -1。
    /// </returns>
    static int FindLowestEntropyCell(NativeArray<ulong> optionMasks, NativeArray<byte> collapsedFlags, int tieBreakSeed, out bool foundContradiction)
    {
        int bestIndex = -1;
        int bestCount = int.MaxValue;
        int tieCount = 0;
        foundContradiction = false;

        for (int i = 0; i < optionMasks.Length; i++)
        {
            if (collapsedFlags[i] != 0)
            {
                continue;
            }

            int count = CountBits(optionMasks[i]);
            if (count == 0)
            {
                foundContradiction = true;
                return i;
            }

            if (count < bestCount)
            {
                bestCount = count;
                bestIndex = i;
                tieCount = 1;
            }
            else if (count == bestCount)
            {
                tieCount++;
                int selectedTie = tieBreakSeed % tieCount;
                if (selectedTie == tieCount - 1)
                {
                    bestIndex = i;
                }
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// mask 内の 1 bit を数える。
    /// </summary>
    /// <param name="mask">
    /// 候補 tile の mask。
    /// </param>
    /// <returns>
    /// 候補 tile 数。
    /// </returns>
    /// <remarks>
    /// 例: <c>0b1011</c> は 3 です。
    /// </remarks>
    static int CountBits(ulong mask)
    {
        int count = 0;

        while (mask != 0UL)
        {
            mask &= mask - 1UL;
            count++;
        }

        return count;
    }

    /// <summary>
    /// WFC を 1 step 進める coroutine。
    /// </summary>
    /// <remarks>
    /// <see cref="WaitForSeconds"/> は見た目を観察しやすくするための待ち時間です。
    /// </remarks>
    IEnumerator CheckEntropy()
    {
        int cellIndex = FindLowestEntropyCell(out bool foundContradiction);

        if (cellIndex < 0)
        {
            yield break;
        }

        if (foundContradiction)
        {
            Debug.LogError($"WFC contradiction: cell {cellIndex} has no valid tile options.");
            yield break;
        }

        yield return new WaitForSeconds(0.01f);

        CollapseCell(cellIndex);
    }

    /// <summary>
    /// 指定セルを 1 つの tile に決める。
    /// </summary>
    /// <param name="cellIndex">
    /// 対象セルの 1 次元 index。
    /// </param>
    /// <remarks>
    /// Random と prefab 生成を使うので main thread 専用です。
    /// </remarks>
    void CollapseCell(int cellIndex)
    {
        ulong options = currentOptions[cellIndex];
        int selectedTileIndex = PickRandomTileIndex(options);

        if (selectedTileIndex < 0)
        {
            return;
        }

        collapsed[cellIndex] = 1;
        collapsedTileIndices[cellIndex] = selectedTileIndex;
        currentOptions[cellIndex] = 1UL << selectedTileIndex;

        Cell cell = gridComponents[cellIndex];
        cell.CreateCell(true, new[] { tileObjects[selectedTileIndex] });
        Instantiate(tileObjects[selectedTileIndex], cell.transform.position, Quaternion.identity);

        UpdateGeneration();
    }

    /// <summary>
    /// 候補から tile id を 1 つ選ぶ。
    /// </summary>
    /// <param name="options">
    /// 候補 tile の mask。
    /// </param>
    /// <returns>
    /// 選ばれた tile id。候補がなければ -1。
    /// </returns>
    /// <remarks>
    /// <see cref="UnityEngine.Random"/> を使うので Job では呼びません。
    /// </remarks>
    int PickRandomTileIndex(ulong options)
    {
        int optionCount = CountBits(options);
        if (optionCount == 0)
        {
            Debug.LogError("No valid tile option.");
            return -1;
        }

        int target = UnityEngine.Random.Range(0, optionCount);

        for (int tileIndex = 0; tileIndex < tileObjects.Length; tileIndex++)
        {
            if ((options & (1UL << tileIndex)) == 0UL)
            {
                continue;
            }

            if (target == 0)
            {
                return tileIndex;
            }

            target--;
        }

        Debug.LogError("No valid tile option.");
        return -1;
    }

    /// <summary>
    /// collapse 結果を伝播して、全セルの候補を更新する。
    /// </summary>
    /// <remarks>
    /// 候補計算は <see cref="UpdateGenerationJob"/> で並列化します。
    /// 完了後に current / next 配列を swap します。
    /// </remarks>
    void UpdateGeneration()
    {
        UpdateGenerationJob job = new UpdateGenerationJob
        {
            CurrentOptions = currentOptions,
            Collapsed = collapsed,
            UpMasks = upMasks,
            RightMasks = rightMasks,
            DownMasks = downMasks,
            LeftMasks = leftMasks,
            NextOptions = nextOptions,
            Dimensions = dimensions,
            TileCount = tileObjects.Length,
            AllOptions = allOptions,
        };

        JobHandle handle = job.Schedule(currentOptions.Length, 64);
        handle.Complete();

        NativeArray<ulong> swap = currentOptions;
        currentOptions = nextOptions;
        nextOptions = swap;

        iterations++;

        if (iterations < dimensions * dimensions)
        {
            StartCoroutine(CheckEntropy());
        }
    }


    /// <summary>
    /// 所有している NativeArray を解放する。
    /// </summary>
    /// <remarks>
    /// <see cref="Allocator.Persistent"/> は GC 対象外なので明示的に Dispose します。
    /// </remarks>
    void OnDestroy()
    {
        if (currentOptions.IsCreated) currentOptions.Dispose();
        if (nextOptions.IsCreated) nextOptions.Dispose();
        if (collapsed.IsCreated) collapsed.Dispose();
        if (collapsedTileIndices.IsCreated) collapsedTileIndices.Dispose();

        if (upMasks.IsCreated) upMasks.Dispose();
        if (rightMasks.IsCreated) rightMasks.Dispose();
        if (downMasks.IsCreated) downMasks.Dispose();
        if (leftMasks.IsCreated) leftMasks.Dispose();
    }

    /// <summary>
    /// 初期化失敗時にも使う NativeArray 解放 helper。
    /// </summary>
    /// <remarks>
    /// 途中まで確保した配列を安全に解放します。
    /// </remarks>
    void DisposeNativeArrays()
    {
        if (currentOptions.IsCreated) currentOptions.Dispose();
        if (nextOptions.IsCreated) nextOptions.Dispose();
        if (collapsed.IsCreated) collapsed.Dispose();
        if (collapsedTileIndices.IsCreated) collapsedTileIndices.Dispose();

        if (upMasks.IsCreated) upMasks.Dispose();
        if (rightMasks.IsCreated) rightMasks.Dispose();
        if (downMasks.IsCreated) downMasks.Dispose();
        if (leftMasks.IsCreated) leftMasks.Dispose();
    }

#if UNITY_INCLUDE_TESTS
    /// <summary>
    /// テストから entropy 選択を呼ぶための入口。
    /// </summary>
    /// <param name="optionMasks">
    /// managed 配列の候補 mask。
    /// </param>
    /// <param name="collapsedFlags">
    /// managed 配列の collapse flag。
    /// </param>
    /// <param name="tieBreakSeed">
    /// tie-break 用 seed。
    /// </param>
    /// <returns>
    /// 選ばれたセル index。
    /// </returns>
    /// <remarks>
    /// 本番では使いません。テスト時だけ公開されます。
    /// </remarks>
    public static int FindLowestEntropyCellForTest(ulong[] optionMasks, byte[] collapsedFlags, int tieBreakSeed)
    {
        NativeArray<ulong> nativeOptions = new NativeArray<ulong>(optionMasks, Allocator.Temp);
        NativeArray<byte> nativeCollapsed = new NativeArray<byte>(collapsedFlags, Allocator.Temp);

        try
        {
            return FindLowestEntropyCell(nativeOptions, nativeCollapsed, tieBreakSeed, out _);
        }
        finally
        {
            if (nativeOptions.IsCreated) nativeOptions.Dispose();
            if (nativeCollapsed.IsCreated) nativeCollapsed.Dispose();
        }
    }
#endif

    /// <summary>
    /// 全セルの候補を並列に更新する Burst Job。
    /// </summary>
    /// <remarks>
    /// Unity object には触らず、数値配列だけを読み書きします。
    /// </remarks>
    [BurstCompile]
    private struct UpdateGenerationJob : IJobParallelFor
    {
        /// <summary>
        /// 前の候補。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> CurrentOptions;

        /// <summary>
        /// collapse 済み flag。
        /// </summary>
        [ReadOnly] public NativeArray<byte> Collapsed;

        /// <summary>
        /// 上方向の許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> UpMasks;

        /// <summary>
        /// 右方向の許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> RightMasks;

        /// <summary>
        /// 下方向の許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> DownMasks;

        /// <summary>
        /// 左方向の許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> LeftMasks;

        /// <summary>
        /// 次の候補。各 job index は自分の場所だけ書きます。
        /// </summary>
        [WriteOnly] public NativeArray<ulong> NextOptions;

        /// <summary>
        /// グリッド一辺のセル数。
        /// </summary>
        public int Dimensions;

        /// <summary>
        /// tile 種類数。
        /// </summary>
        public int TileCount;

        /// <summary>
        /// 全 tile が候補の mask。
        /// </summary>
        public ulong AllOptions;

        /// <summary>
        /// 1 セル分の次の候補を計算する。
        /// </summary>
        /// <param name="index">
        /// 処理するセル index。
        /// </param>
        /// <remarks>
        /// 右隣を見るときは、右隣から見た左側ルールを使います。
        /// 方向は「相手から見た自分の位置」で読みます。
        /// </remarks>
        public void Execute(int index)
        {
            if (Collapsed[index] != 0)
            {
                NextOptions[index] = CurrentOptions[index];
                return;
            }

            int x = index % Dimensions;
            int y = index / Dimensions;

            ulong options = AllOptions;

            if (y > 0)
            {
                int belowIndex = x + (y - 1) * Dimensions;
                options &= BuildValidMask(CurrentOptions[belowIndex], UpMasks);
            }

            if (x < Dimensions - 1)
            {
                int rightIndex = x + 1 + y * Dimensions;
                options &= BuildValidMask(CurrentOptions[rightIndex], LeftMasks);
            }

            if (y < Dimensions - 1)
            {
                int aboveIndex = x + (y + 1) * Dimensions;
                options &= BuildValidMask(CurrentOptions[aboveIndex], DownMasks);
            }

            if (x > 0)
            {
                int leftIndex = x - 1 + y * Dimensions;
                options &= BuildValidMask(CurrentOptions[leftIndex], RightMasks);
            }

            NextOptions[index] = options;
        }

        /// <summary>
        /// 隣人候補から、現在セルに置ける tile mask を作る。
        /// </summary>
        /// <param name="neighborOptions">
        /// 隣人セルの候補。
        /// </param>
        /// <param name="directionMasks">
        /// 隣人から見て、こちら側に置ける tile mask。
        /// </param>
        /// <returns>
        /// 現在セルに置ける tile mask。
        /// </returns>
        /// <remarks>
        /// 複数候補は OR でまとめます。
        /// </remarks>
        private ulong BuildValidMask(ulong neighborOptions, NativeArray<ulong> directionMasks)
        {
            ulong validMask = 0UL;

            for (int tileIndex = 0; tileIndex < TileCount; tileIndex++)
            {
                if ((neighborOptions & (1UL << tileIndex)) != 0UL)
                {
                    validMask |= directionMasks[tileIndex];
                }
            }

            return validMask;
        }
    }
}
