using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;

/// <summary>
/// Wave Function Collapse で 2D グリッド上に tile prefab を生成する MonoBehaviour。
/// </summary>
/// <remarks>
/// このクラスは Unity object を扱う main thread 側の owner です。
/// Prefab 生成、Transform 参照、Random、Coroutine はこのクラス内で main thread 上に閉じ込めます。
///
/// WFC の候補集合は <see cref="Tile"/> 配列ではなく <c>ulong</c> bit mask として保持します。
/// tile index <c>i</c> が候補に含まれる場合、mask の <c>1UL &lt;&lt; i</c> が立ちます。
/// これにより、候補の intersection は bitwise AND で処理でき、Job / Burst に渡せる
/// unmanaged data だけで候補更新を並列化できます。
///
/// 重要な境界:
/// - <see cref="Tile"/> / <see cref="Cell"/> / <see cref="GameObject"/> は Job に渡さない。
/// - <see cref="NativeArray{T}"/> はこのクラスが所有し、<see cref="OnDestroy"/> で解放する。
/// - <see cref="UpdateGenerationJob"/> は前世代配列を読み、次世代配列だけを書き込む。
/// </remarks>
/// <note type="caller">
/// Inspector では <see cref="dimensions"/>, <see cref="tileObjects"/>, <see cref="cellObj"/> を設定してください。
/// <see cref="tileObjects"/> は 1..64 個までです。64 個を超える場合は <c>ulong</c> 1 個では表せないため、
/// 複数 word の mask へ設計変更が必要です。
/// </note>
public class WaveFunction : MonoBehaviour
{
    /// <summary>
    /// <c>ulong</c> 1 個で表せる tile 種類数の上限。
    /// </summary>
    /// <remarks>
    /// 1 tile を 1 bit で表すため、64 bit の <c>ulong</c> では最大 64 種類です。
    /// tile 数が 64 の場合だけ <c>1UL &lt;&lt; 64</c> は使えないため、
    /// <see cref="BuildAllOptionsMask"/> で <see cref="ulong.MaxValue"/> を使います。
    /// </remarks>
    private const int MaxTileCount = 64;

    /// <summary>
    /// 生成するグリッドの一辺のセル数。
    /// </summary>
    /// <remarks>
    /// 総セル数は <c>dimensions * dimensions</c> です。
    /// <see cref="TryValidateConfig"/> で 1 以上かつ <see cref="int.MaxValue"/> を超えないことを確認します。
    /// </remarks>
    public int dimensions;

    /// <summary>
    /// WFC が扱う tile prefab の一覧。
    /// </summary>
    /// <remarks>
    /// 配列 index が tile id になります。
    /// 全ての neighbor 参照は、この配列内のいずれかの要素を指している必要があります。
    /// </remarks>
    public Tile[] tileObjects;

    /// <summary>
    /// 生成した Cell marker の一覧。
    /// </summary>
    /// <remarks>
    /// 計算本体ではなく Scene / Inspector 確認用の GameObject 参照です。
    /// WFC の実状態は <see cref="currentOptions"/> と <see cref="collapsed"/> が所有します。
    /// </remarks>
    public List<Cell> gridComponents;

    /// <summary>
    /// 各グリッド位置に配置する Cell marker prefab。
    /// </summary>
    /// <remarks>
    /// 実際の tile prefab とは別です。
    /// Cell は計算結果を表示するための軽い marker として使います。
    /// </remarks>
    public Cell cellObj;

    /// <summary>
    /// 隣接セル中心間の world unit 距離。
    /// </summary>
    /// <remarks>
    /// Sprite の Pixels Per Unit と合わせてください。
    /// 16x16 px の tile を 1 unit にしたい場合、Sprite 側の PPU は 16、ここは 1.0 にします。
    /// </remarks>
    [SerializeField] private float cellSize = 1.0f;

    /// <summary>
    /// マップ中心を合わせる対象 Camera。
    /// </summary>
    /// <remarks>
    /// null の場合は、この GameObject の Transform 位置を中心として使います。
    /// Camera 参照は main thread でだけ読み、Job には渡しません。
    /// </remarks>
    [SerializeField] private Camera targetCamera;

    /// <summary>
    /// collapse と伝播を何回進めたかを数える実行時状態。
    /// </summary>
    /// <remarks>
    /// 最大でセル数分だけ collapse すれば全セルが決まるため、
    /// <see cref="UpdateGeneration"/> の最後で停止条件に使います。
    /// </remarks>
    int iterations = 0;

    /// <summary>
    /// 現世代の各セル候補 mask。
    /// </summary>
    /// <remarks>
    /// 所有者はこの <see cref="WaveFunction"/> です。
    /// Job へは読み取り専用として渡し、main thread では collapse 済みセルの mask を 1 tile に固定します。
    /// </remarks>
    private NativeArray<ulong> currentOptions;

    /// <summary>
    /// 次世代の各セル候補 mask。
    /// </summary>
    /// <remarks>
    /// <see cref="UpdateGenerationJob"/> が index ごとに 1 回だけ書き込みます。
    /// Job 完了後、<see cref="currentOptions"/> と参照を swap します。
    /// </remarks>
    private NativeArray<ulong> nextOptions;

    /// <summary>
    /// 各セルが collapse 済みかどうかを 0/1 で持つ配列。
    /// </summary>
    /// <remarks>
    /// <c>bool</c> ではなく <c>byte</c> にして、Job / Burst 側で明示的な unmanaged flag として扱います。
    /// 0 は未 collapse、非 0 は collapse 済みです。
    /// </remarks>
    private NativeArray<byte> collapsed;

    /// <summary>
    /// collapse 済みセルに選ばれた tile index。
    /// </summary>
    /// <remarks>
    /// 未 collapse のセルは -1 です。
    /// 現在は debug / 将来拡張用の状態で、tile prefab の Instantiate は <see cref="CollapseCell"/> で行います。
    /// </remarks>
    private NativeArray<int> collapsedTileIndices;

    /// <summary>
    /// tile ごとの「上側に置ける tile 集合」を表す mask 配列。
    /// </summary>
    private NativeArray<ulong> upMasks;

    /// <summary>
    /// tile ごとの「右側に置ける tile 集合」を表す mask 配列。
    /// </summary>
    private NativeArray<ulong> rightMasks;

    /// <summary>
    /// tile ごとの「下側に置ける tile 集合」を表す mask 配列。
    /// </summary>
    private NativeArray<ulong> downMasks;

    /// <summary>
    /// tile ごとの「左側に置ける tile 集合」を表す mask 配列。
    /// </summary>
    private NativeArray<ulong> leftMasks;

    /// <summary>
    /// 全 tile が候補である状態を表す mask。
    /// </summary>
    /// <remarks>
    /// 初期セル候補と、各世代の候補再計算の出発点に使います。
    /// </remarks>
    private ulong allOptions;

    /// <summary>
    /// Unity の初期化時に WFC 用の設定検証、mask 変換、実行時配列確保、グリッド生成を行う。
    /// </summary>
    /// <remarks>
    /// ここはリソース owner の初期化境界です。
    /// <see cref="NativeArray{T}"/> を <see cref="Allocator.Persistent"/> で確保するため、
    /// 確保後の失敗時は <see cref="DisposeNativeArrays"/> で明示的に解放します。
    ///
    /// Caller:
    /// - Unity が呼び出します。手動で呼ばないでください。
    /// - 設定ミスがある場合は <see cref="enabled"/> を false にして以降の生成を止めます。
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
    /// Inspector から渡された WFC 設定が実行可能か検査する。
    /// </summary>
    /// <returns>
    /// 設定が有効なら true。
    /// 実行前に検出できる設定ミスがあれば false。
    /// </returns>
    /// <remarks>
    /// 外部入力である Inspector 設定は assert ではなく runtime check で扱います。
    /// ここで検出するのは、配列確保や bit shift の前に止めるべき境界条件です。
    ///
    /// Caller:
    /// - false が返った場合、呼び出し側は生成を開始してはいけません。
    /// - false 時点では NativeArray をまだ確保していない想定です。
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
    /// tile 数から「全 tile が候補」の bit mask を作る。
    /// </summary>
    /// <param name="tileCount">
    /// tile 種類数。1..64 を想定します。
    /// </param>
    /// <returns>
    /// tile index 0 から <paramref name="tileCount"/> - 1 までの bit が立った mask。
    /// </returns>
    /// <remarks>
    /// <c>1UL &lt;&lt; 64</c> は C# の shift 規則により期待した 65bit 目にはなりません。
    /// そのため 64 種類ちょうどの場合は特別に <see cref="ulong.MaxValue"/> を返します。
    /// </remarks>
    static ulong BuildAllOptionsMask(int tileCount)
    {
        return tileCount == MaxTileCount
            ? ulong.MaxValue
            : (1UL << tileCount) - 1UL;
    }

    /// <summary>
    /// 全 tile の neighbor 配列を Job 用の bit mask 配列へ変換する。
    /// </summary>
    /// <returns>
    /// 全方向の mask を作れた場合は true。
    /// null や <see cref="tileObjects"/> 外参照が見つかった場合は false。
    /// </returns>
    /// <remarks>
    /// この関数は <see cref="upMasks"/> / <see cref="rightMasks"/> /
    /// <see cref="downMasks"/> / <see cref="leftMasks"/> を確保して書き込みます。
    ///
    /// Caller:
    /// - false が返った場合は <see cref="DisposeNativeArrays"/> を呼んで、途中まで確保した配列を解放してください。
    /// - 成功後の mask は実行中に更新しません。tile 接続規則は初期化時固定です。
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
    /// 1 方向分の neighbor tile 配列を bit mask へ変換する。
    /// </summary>
    /// <param name="tiles">
    /// 変換対象の neighbor 配列。
    /// 各要素は <see cref="tileObjects"/> に含まれている必要があります。
    /// </param>
    /// <param name="owner">
    /// エラー表示用の owner tile。
    /// </param>
    /// <param name="directionName">
    /// エラー表示用の方向名。
    /// </param>
    /// <param name="mask">
    /// 成功時に、neighbor tile index の bit が立った mask を書き込みます。
    /// 失敗時は 0 のままです。
    /// </param>
    /// <returns>
    /// 変換できたら true。
    /// null 参照や未登録 tile 参照があれば false。
    /// </returns>
    /// <remarks>
    /// この関数は設定データを計算データへ変換するプロトコル境界です。
    /// 設定ミスを黙って無視すると候補集合が欠け、後続の WFC で contradiction が起きやすくなります。
    /// そのため、不正な参照は初期化時エラーとして明示的に止めます。
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
    /// Cell marker をグリッド状に生成し、最初の WFC step を開始する。
    /// </summary>
    /// <remarks>
    /// ここで生成するのは計算用データではなく、Scene 上の位置 marker です。
    /// 各 marker の候補表示は空配列で初期化します。
    ///
    /// 配置座標は map 全体の中心が <see cref="targetCamera"/> またはこの GameObject の位置に来るようにします。
    /// <see cref="cellSize"/> は隣接 marker の中心間距離です。
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
    /// 次に collapse するセル index を探し、contradiction の有無も返す。
    /// </summary>
    /// <param name="foundContradiction">
    /// 候補数 0 の未 collapse セルを見つけた場合 true。
    /// </param>
    /// <returns>
    /// collapse 対象または contradiction が起きたセル index。
    /// 未 collapse セルがない場合は -1。
    /// </returns>
    /// <remarks>
    /// entropy は候補 tile 数です。
    /// 最小 entropy のセルを選ぶことで、制約が強い場所から決定し、後続の contradiction を減らします。
    /// 同じ entropy のセルが複数ある場合は、固定順による偏りを避けるためランダム seed で tie-break します。
    /// </remarks>
    int FindLowestEntropyCell(out bool foundContradiction)
    {
        int tieBreakSeed = UnityEngine.Random.Range(0, int.MaxValue);
        return FindLowestEntropyCell(currentOptions, collapsed, tieBreakSeed, out foundContradiction);
    }

    /// <summary>
    /// 候補 mask 配列から最小 entropy のセル index を選ぶ純粋ロジック。
    /// </summary>
    /// <param name="optionMasks">
    /// 各セルの候補 tile bit mask。
    /// 読み取り専用として扱います。
    /// </param>
    /// <param name="collapsedFlags">
    /// 各セルの collapse 済み flag。
    /// 0 は未 collapse、非 0 は collapse 済みです。
    /// </param>
    /// <param name="tieBreakSeed">
    /// 同 entropy 候補から選ぶための seed。
    /// テストでは固定値を渡せます。
    /// </param>
    /// <param name="foundContradiction">
    /// 候補数 0 の未 collapse セルを見つけた場合 true。
    /// </param>
    /// <returns>
    /// 選択されたセル index。
    /// 未 collapse セルがない場合は -1。
    /// </returns>
    /// <remarks>
    /// この関数は UnityEngine.Object に触らないため、テストしやすい計算境界です。
    /// ただし現在は main thread 側で呼びます。
    /// </remarks>
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
    /// mask 内で 1 になっている bit の数を数える。
    /// </summary>
    /// <param name="mask">
    /// 候補 tile 集合を表す bit mask。
    /// </param>
    /// <returns>
    /// 候補 tile 数。
    /// </returns>
    /// <remarks>
    /// <c>mask &amp;= mask - 1</c> は最下位の 1 bit を 1 つ消す定番の bit 操作です。
    /// ループ回数は tile 上限 64 回なので、ここでは十分小さい固定コストです。
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
    /// 1 step 分の WFC 生成を時間差で進める coroutine。
    /// </summary>
    /// <remarks>
    /// この coroutine は CPU 並列化ではありません。
    /// <see cref="WaitForSeconds"/> で見た目の生成速度を落とし、
    /// collapse の進行を Scene 上で観察しやすくしています。
    ///
    /// 状態遷移:
    /// - 全セル collapse 済みなら終了。
    /// - contradiction が見つかったら error を出して終了。
    /// - collapse 可能なセルがあれば少し待って <see cref="CollapseCell"/> へ進む。
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
    /// 指定セルを 1 つの tile に collapse し、対応する prefab を生成する。
    /// </summary>
    /// <param name="cellIndex">
    /// collapse 対象のセル index。
    /// <c>x + y * dimensions</c> で作られる 1 次元 index です。
    /// </param>
    /// <remarks>
    /// この関数は main thread 専用です。
    /// <see cref="UnityEngine.Random"/>、<see cref="Transform"/>、<see cref="Object.Instantiate(Object)"/>
    /// は Job / Burst から使えないため、collapse と prefab 生成はここに集約します。
    ///
    /// post:
    /// - <see cref="collapsed"/> の対象 index が 1 になります。
    /// - <see cref="currentOptions"/> の対象 index は選択 tile 1 つだけの mask になります。
    /// - Scene 上に選択 tile prefab が生成されます。
    /// - 続けて <see cref="UpdateGeneration"/> で周囲の候補を伝播します。
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
    /// 候補 mask からランダムに tile index を 1 つ選ぶ。
    /// </summary>
    /// <param name="options">
    /// 候補 tile 集合を表す bit mask。
    /// </param>
    /// <returns>
    /// 選ばれた tile index。
    /// 候補が 0 個の場合は -1。
    /// </returns>
    /// <remarks>
    /// この関数は main thread 側でのみ呼びます。
    /// UnityEngine.Random は Burst Job 内で使えないためです。
    ///
    /// mask の中で立っている bit だけを数え、0..候補数-1 のランダム位置に対応する
    /// tile index を返します。
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
    /// collapse 結果を全セルへ伝播し、次世代候補 mask を計算する。
    /// </summary>
    /// <remarks>
    /// ここが WFC の並列化対象です。
    /// 各セルの次候補は、前世代の上下左右セルの候補だけから計算できます。
    /// そのため <see cref="UpdateGenerationJob"/> では index ごとに独立して
    /// <see cref="nextOptions"/> へ書き込みます。
    ///
    /// 現在は <see cref="JobHandle.Complete"/> を同じ関数内で呼んでいるため、
    /// main thread は Job 完了まで待ちます。ただし候補計算そのものは worker thread に分散されます。
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
    /// この MonoBehaviour が破棄されるとき、所有している NativeArray を解放する。
    /// </summary>
    /// <remarks>
    /// <see cref="Allocator.Persistent"/> で確保した配列は GC では解放されません。
    /// Unity の NativeArray safety check は未解放を検出しますが、実運用では明示解放が責務です。
    ///
    /// Caller:
    /// - Unity が呼び出します。手動で呼ぶ必要はありません。
    /// - 二重解放を避けるため <see cref="NativeArray{T}.IsCreated"/> を確認します。
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
    /// 初期化失敗時にも使える NativeArray 解放 helper。
    /// </summary>
    /// <remarks>
    /// <see cref="Awake"/> の途中で mask 作成に失敗した場合など、
    /// <see cref="OnDestroy"/> まで待たずに確保済み配列を解放するための関数です。
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
    /// EditMode test から entropy 選択ロジックを検証するための入口。
    /// </summary>
    /// <param name="optionMasks">
    /// managed 配列で渡す候補 mask。
    /// テスト用に一時的な NativeArray へコピーします。
    /// </param>
    /// <param name="collapsedFlags">
    /// managed 配列で渡す collapse flag。
    /// </param>
    /// <param name="tieBreakSeed">
    /// 同 entropy の tie-break を固定する seed。
    /// </param>
    /// <returns>
    /// <see cref="FindLowestEntropyCell(NativeArray{ulong}, NativeArray{byte}, int, out bool)"/>
    /// が選んだセル index。
    /// </returns>
    /// <remarks>
    /// 本番コードからは使いません。
    /// <c>UNITY_INCLUDE_TESTS</c> が定義されるテスト用 build にだけ公開されます。
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
    /// 全セルの候補 mask を並列に更新する Burst 対応 Job。
    /// </summary>
    /// <remarks>
    /// この Job は Unity object に一切触りません。
    /// 入力は <see cref="NativeArray{T}"/> と primitive 値だけです。
    ///
    /// データフロー:
    /// - <see cref="CurrentOptions"/> と <see cref="Collapsed"/> を読む。
    /// - 隣接規則 mask を読む。
    /// - 自分の index に対応する <see cref="NextOptions"/> だけを書く。
    ///
    /// これにより、複数 worker が同時に走っても同じ index を競合して書きません。
    /// </remarks>
    [BurstCompile]
    private struct UpdateGenerationJob : IJobParallelFor
    {
        /// <summary>
        /// 前世代の候補 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> CurrentOptions;

        /// <summary>
        /// collapse 済み flag。
        /// 0 は未 collapse、非 0 は collapse 済み。
        /// </summary>
        [ReadOnly] public NativeArray<byte> Collapsed;

        /// <summary>
        /// tile ごとの上方向許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> UpMasks;

        /// <summary>
        /// tile ごとの右方向許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> RightMasks;

        /// <summary>
        /// tile ごとの下方向許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> DownMasks;

        /// <summary>
        /// tile ごとの左方向許可 mask。
        /// </summary>
        [ReadOnly] public NativeArray<ulong> LeftMasks;

        /// <summary>
        /// 次世代の候補 mask。
        /// 各 Job index は自分の index のみを書きます。
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
        /// 全 tile が候補である初期 mask。
        /// </summary>
        public ulong AllOptions;

        /// <summary>
        /// 1 セル分の次世代候補 mask を計算する。
        /// </summary>
        /// <param name="index">
        /// 処理対象セルの 1 次元 index。
        /// </param>
        /// <remarks>
        /// 隣人の候補 mask から「隣人が許可する、現在セルに置ける tile 集合」を作り、
        /// 4 方向分を AND して候補を絞ります。
        ///
        /// 方向の読み方:
        /// - 右隣を見る場合、現在セルは右隣から見て左側にあるため <see cref="LeftMasks"/> を使います。
        /// - 左隣を見る場合、現在セルは左隣から見て右側にあるため <see cref="RightMasks"/> を使います。
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
                int upIndex = x + (y - 1) * Dimensions;
                options &= BuildValidMask(CurrentOptions[upIndex], UpMasks);
            }

            if (x < Dimensions - 1)
            {
                int rightIndex = x + 1 + y * Dimensions;
                options &= BuildValidMask(CurrentOptions[rightIndex], LeftMasks);
            }

            if (y < Dimensions - 1)
            {
                int downIndex = x + (y + 1) * Dimensions;
                options &= BuildValidMask(CurrentOptions[downIndex], DownMasks);
            }

            if (x > 0)
            {
                int leftIndex = x - 1 + y * Dimensions;
                options &= BuildValidMask(CurrentOptions[leftIndex], RightMasks);
            }

            NextOptions[index] = options;
        }

        /// <summary>
        /// 隣人候補 mask から、現在セルに許可される tile mask を作る。
        /// </summary>
        /// <param name="neighborOptions">
        /// 隣人セルが取り得る tile 候補 mask。
        /// </param>
        /// <param name="directionMasks">
        /// 隣人 tile から見て、現在セル方向に置ける tile mask の配列。
        /// </param>
        /// <returns>
        /// 隣人候補のどれかと接続可能な現在セル tile の集合。
        /// </returns>
        /// <remarks>
        /// 複数の隣人候補がある場合は OR でまとめます。
        /// その後、呼び出し側で他方向の制約と AND することで候補を狭めます。
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
