using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WFC で使う tile と、その接続ルール。
/// </summary>
/// <remarks>
/// 各配列は「この tile から見て、その方向に置ける tile」です。
/// <see cref="WaveFunction"/> は初期化時にこれを bit mask へ変換します。
/// </remarks>
public class Tile : MonoBehaviour
{
    /// <summary>
    /// この tile の上側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// Inspector で設定します。実行中に変えても生成中の mask には反映されません。
    /// </remarks>
    public Tile[] upNeighbours;

    /// <summary>
    /// この tile の右側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// 方向名は常に「この tile から見た方向」です。
    /// </remarks>
    public Tile[] rightNeighbours;

    /// <summary>
    /// この tile の下側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// up / down は見た目の上下方向を表します。
    /// </remarks>
    public Tile[] downNeighbours;

    /// <summary>
    /// この tile の左側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// 参照順は結果に影響しません。
    /// </remarks>
    public Tile[] leftNeighbours;
}
