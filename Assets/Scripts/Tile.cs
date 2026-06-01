using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WFC で配置可能な tile prefab の接続規則を保持するコンポーネント。
/// </summary>
/// <remarks>
/// この型は「この tile の各方向に、どの tile を置けるか」というプロトコル境界を表します。
/// たとえば <see cref="rightNeighbours"/> は、この tile の右側に置ける tile の一覧です。
///
/// 実行時の高速な候補更新では、<see cref="WaveFunction"/> がこれらの配列を一度だけ読み取り、
/// <c>ulong</c> bit mask に変換します。Job / Burst 側は <see cref="Tile"/> や
/// <see cref="GameObject"/> 参照を読まず、変換済みの mask だけを処理します。
/// </remarks>
/// <note type="caller">
/// Inspector では全方向の配列に null を入れないでください。
/// また、各配列に入れる tile は <see cref="WaveFunction.tileObjects"/> に含まれている必要があります。
/// 含まれていない参照は初期化時の設定エラーとして扱われます。
/// </note>
public class Tile : MonoBehaviour
{
    /// <summary>
    /// この tile の上側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// 配列は設定データとして扱います。実行中に変更しても、
    /// 既に <see cref="WaveFunction"/> が作成した mask には反映されません。
    /// </remarks>
    public Tile[] upNeighbours;

    /// <summary>
    /// この tile の右側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// WFC の候補更新では、右隣の tile から見た「左側に置ける tile」と、
    /// 現在セルの候補を照合する場面があります。方向名は常に「この tile から見た方向」です。
    /// </remarks>
    public Tile[] rightNeighbours;

    /// <summary>
    /// この tile の下側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// Unity の world 座標では通常 y が増えると上方向です。
    /// 配列名の up / down と、グリッド index の y + 1 / y - 1 の対応を読むときは、
    /// 「隣人から見た現在セルの方向」も同時に確認してください。
    /// </remarks>
    public Tile[] downNeighbours;

    /// <summary>
    /// この tile の左側に置ける tile の一覧。
    /// </summary>
    /// <remarks>
    /// 配列内の参照順は WFC の意味には影響しません。
    /// 実行時は tile index に対応する bit を立てるだけなので、重複参照は同じ bit に畳み込まれます。
    /// </remarks>
    public Tile[] leftNeighbours;
}
