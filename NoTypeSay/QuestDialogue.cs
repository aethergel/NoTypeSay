using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace NoTypeSay;

[Sheet("QuestDialogue")]
public readonly struct QuestDialogue(RawRow row) : IExcelRow<QuestDialogue>
{
    public uint RowId => row.RowId;

    public ReadOnlySeString Key => row.ReadStringColumn(0);

    public ReadOnlySeString Value => row.ReadStringColumn(1);

    static QuestDialogue IExcelRow<QuestDialogue>.Create( ExcelPage page, uint offset, uint row ) =>
        new(new RawRow(page, offset, row));
}
