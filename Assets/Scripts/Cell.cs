using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WFC グリッド上の 1 マスを表す表示用コンポーネント。
/// </summary>
/// <remarks>
/// この型は WFC の本体データ owner ではありません。
/// 現在の候補集合や collapse 済み状態の実データは <see cref="WaveFunction"/> が
/// <c>NativeArray</c> で所有します。
///
/// このコンポーネントは Unity Scene / Inspector 上で各セルの状態を確認しやすくするための
/// debug view に近い役割です。Job / Burst 側から <see cref="MonoBehaviour"/> や
/// <see cref="Tile"/> 参照へ触ることはできないため、計算境界にはこの型を渡しません。
/// </remarks>
/// <note type="caller">
/// 呼び出し側は <see cref="tileOptions"/> を WFC の信頼できる計算元として扱わないでください。
/// 生成アルゴリズムの正しい状態は <see cref="WaveFunction"/> 内部の bit mask 配列にあります。
/// </note>
public class Cell : MonoBehaviour
{
    /// <summary>
    /// このセルが最終的な tile に collapse 済みかどうかを Inspector 表示するための値。
    /// </summary>
    /// <remarks>
    /// 実際の collapse 状態は <see cref="WaveFunction"/> の <c>collapsed</c> 配列が所有します。
    /// この値は main thread で <see cref="CreateCell"/> から同期される debug 用の複製です。
    /// </remarks>
    public bool collapsed;

    /// <summary>
    /// Inspector 表示用の候補 tile 配列。
    /// </summary>
    /// <remarks>
    /// 候補集合の実体は <c>ulong</c> bit mask です。
    /// この配列は「collapse 後に選ばれた tile を見せる」用途に限定しています。
    /// 未 collapse 状態では空配列を入れ、null を保存しないことで Inspector / caller 側の
    /// null 分岐を減らします。
    /// </remarks>
    public Tile[] tileOptions = Array.Empty<Tile>();

    /// <summary>
    /// Inspector 表示用のセル状態を初期化または同期する。
    /// </summary>
    /// <param name="collapseState">
    /// Inspector に表示する collapse 状態。
    /// WFC の信頼できる状態は <see cref="WaveFunction"/> 側の配列が持ちます。
    /// </param>
    /// <param name="tiles">
    /// Inspector に表示する tile 参照。
    /// この関数は配列の所有権を取得せず、参照をそのまま保持します。
    /// null が渡された場合は空配列として扱います。
    /// </param>
    /// <remarks>
    /// Caller:
    /// - 渡した配列を後から変更すると、この Cell の表示内容も同じ配列参照経由で変わります。
    /// - 長期的な所有が必要な場合は caller 側で別配列を作って渡してください。
    /// - Job / Burst からこの関数を呼んではいけません。Unity object は main thread 専用です。
    /// </remarks>
    public void CreateCell(bool collapseState, Tile[] tiles)
    {
        collapsed = collapseState;
        tileOptions = tiles ?? Array.Empty<Tile>();
    }

    /// <summary>
    /// Inspector 表示用の候補 tile だけを更新する。
    /// </summary>
    /// <param name="tiles">
    /// 表示する tile 参照の配列。
    /// 所有権は移動せず、この Cell は配列参照を保持するだけです。
    /// null の場合は空配列として保存します。
    /// </param>
    /// <remarks>
    /// 旧 List / Tile[] ベース実装との互換用に残している小さな setter です。
    /// 現在の NativeArray + bit mask 実装では、通常の候補更新ではこの関数を使いません。
    /// </remarks>
    public void RecreateCell(Tile[] tiles)
    {
        tileOptions = tiles ?? Array.Empty<Tile>();
    }
}
