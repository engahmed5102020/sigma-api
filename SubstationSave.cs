using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Sigma.Api;

/// <summary>أعمدة جدول substation المرسلة من التطبيق (أسماء JSON = أسماء الأعمدة).</summary>
internal static class SubstationSave
{
    public static readonly string[] ColumnNames =
    [
        "code", "fyear", "m_code", "r_code", "s_code", "t_code", "date", "date1", "ss_no", "ss_no1",
        "ss_format", "ss_format1", "qty_sub", "qty_sub1", "minipeler_F", "minipeler_F1", "type_sub", "type_sub1",
        "u_code", "ur_code", "l_code", "i_ode", "workstate_type", "workstate_type1", "workstate_finsh", "workstate_finsh1",
        "workstate", "servation_num", "servation_type", "servation_type1", "reservation_type", "reservation_type1",
        "fil_format_m", "fil_format_s", "fil_typ", "fil_typ1", "fil_num", "fil_num1", "explatation", "fault", "ss_state",
        "ss_state1", "attatchmentsap", "attatchmentsap1", "attatchmentteam", "attatchmentteam1", "before_path", "after_path",
        "mea_path", "ss_location", "ss_location1", "signteuer", "signteuer1", "qty_band", "g_work", "g_work1", "g_workdate",
        "g_workdate1", "g_workday", "total_befor_twara", "val_twara", "total_after_twara", "twara", "twara1", "srfmoad",
        "srfmoad1", "srf_number", "notfic", "notfic1", "notfic_qty", "notfic_qty1", "notficdate", "notficdate1", "order_no",
        "order_type", "order_type1", "orderdate", "orderdate1", "po", "podate", "podate1", "inv", "invdate", "invdate1",
        "Main_cont_num", "Main_vindor", "cons_cont_num", "cons_vindor", "ss_enter_state", "ss_enter_state1", "ss_enter_time",
        "ss_reviewed", "ss_reviewed1", "state", "state1", "state2", "state3", "state4", "state5", "state6", "state7",
        "state8", "notes",
    ];

    public static SqlParameter MakeParameter(string columnName, JsonElement el)
    {
        var pName = "@" + columnName;
        return el.ValueKind switch
        {
            JsonValueKind.True => new SqlParameter(pName, SqlDbType.Bit) { Value = true },
            JsonValueKind.False => new SqlParameter(pName, SqlDbType.Bit) { Value = false },
            JsonValueKind.Number when el.TryGetInt32(out var i32) => new SqlParameter(pName, SqlDbType.Int) { Value = i32 },
            JsonValueKind.Number when el.TryGetInt64(out var i64) => new SqlParameter(pName, SqlDbType.BigInt) { Value = i64 },
            JsonValueKind.Number => new SqlParameter(pName, SqlDbType.Float) { Value = el.GetDouble() },
            JsonValueKind.String => new SqlParameter(pName, SqlDbType.NVarChar, -1)
            {
                // سلسلة JSON "" تُخزَّن كفارغ في SQL وليس NULL.
                Value = el.GetString() ?? string.Empty,
            },
            JsonValueKind.Null => new SqlParameter(pName, SqlDbType.NVarChar, -1) { Value = DBNull.Value },
            _ => new SqlParameter(pName, SqlDbType.NVarChar, -1) { Value = el.ToString() },
        };
    }
}
