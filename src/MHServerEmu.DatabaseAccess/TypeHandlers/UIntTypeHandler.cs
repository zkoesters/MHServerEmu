using System.Data;
using Dapper;

namespace MHServerEmu.DatabaseAccess.TypeHandlers;

public class UIntTypeHandler : SqlMapper.TypeHandler<uint>
{
    public override void SetValue(IDbDataParameter parameter, uint value)
    {
        parameter.Value = (int)value;
        parameter.DbType = DbType.Int32;
    }

    public override uint Parse(object value)
    {
        return Convert.ToUInt32(value);
    }
}