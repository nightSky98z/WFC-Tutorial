using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scene / Inspector に表示する 1 マス分の情報。
/// </summary>
/// <remarks>
/// WFC の本当の状態は <see cref="WaveFunction"/> の配列が持ちます。
/// このクラスは学習・確認用の表示係です。
/// </remarks>
public class Cell : MonoBehaviour
{
    /// <summary>
    /// このマスの tile が決まったかどうか。
    /// </summary>
    /// <remarks>
    /// collapse 済み = 候補が 1 つに決まった状態です。
    /// </remarks>
    public bool collapsed;

    /// <summary>
    /// Inspector に見せる tile。
    /// </summary>
    /// <remarks>
    /// 未 collapse では空、collapse 後は選ばれた tile が 1 つ入ります。
    /// 計算本体ではなく表示用です。
    /// </remarks>
    public Tile[] tileOptions = Array.Empty<Tile>();

    /// <summary>
    /// Inspector 表示を更新する。
    /// </summary>
    /// <param name="collapseState">
    /// 表示する collapse 状態。
    /// </param>
    /// <param name="tiles">
    /// 表示する tile 配列。null は空配列として扱います。
    /// </param>
    /// <remarks>
    /// 配列はコピーせず参照を保持します。
    /// Unity object を触るので main thread から呼びます。
    /// </remarks>
    public void CreateCell(bool collapseState, Tile[] tiles)
    {
        collapsed = collapseState;
        tileOptions = tiles ?? Array.Empty<Tile>();
    }

    /// <summary>
    /// Inspector 表示用の tile だけを更新する。
    /// </summary>
    /// <param name="tiles">
    /// 表示する tile 配列。null は空配列として扱います。
    /// </param>
    /// <remarks>
    /// 古い実装との互換用です。現在の WFC 計算では通常使いません。
    /// </remarks>
    public void RecreateCell(Tile[] tiles)
    {
        tileOptions = tiles ?? Array.Empty<Tile>();
    }
}
