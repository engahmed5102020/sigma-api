using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Sigma.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 50L * 1024 * 1024;
});

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

async Task<IResult> PostSubstationSaveAsync(HttpRequest request, IConfiguration config, ILoggerFactory lf)
{
    var cs = config.GetConnectionString("DefaultConnection");
    // نفس الاستعلام في appsettings؛ إن غاب المفتاح على السيرفر (نسخة قديمة من appsettings.json) لا يتعطل الحفظ.
    var existsSql = config["SigmaQueries:SubstationSsFormat1ExistsSql"]?.Trim();
    if (string.IsNullOrWhiteSpace(existsSql))
    {
        existsSql = "SELECT TOP (1) 1 FROM substation WHERE ss_format1 = @ss_format1";
    }

    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    using var bodyReader = new StreamReader(request.Body);
    var bodyText = await bodyReader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(bodyText))
    {
        return Results.BadRequest(new { error = "الجسم فارغ." });
    }

    using var doc = JsonDocument.Parse(
        bodyText,
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    var root = doc.RootElement;

    if (!root.TryGetProperty("ss_format1", out var sfEl) || sfEl.ValueKind != JsonValueKind.String)
    {
        return Results.BadRequest(new { error = "ss_format1 مطلوب كنص." });
    }

    var ssFormat1 = sfEl.GetString()?.Trim() ?? "";
    if (ssFormat1.Length == 0)
    {
        return Results.BadRequest(new { error = "ss_format1 مطلوب." });
    }

    try
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var tx = conn.BeginTransaction(IsolationLevel.Serializable);

        try
        {
            if (await ExistsScalarAsync(
                    conn,
                    tx,
                    existsSql,
                    [new SqlParameter("@ss_format1", ssFormat1)]))
            {
                await tx.RollbackAsync();
                return Results.Json(
                    new
                    {
                        error = "duplicate_ss_format1",
                        message = "تم إدخال المحطة من قبل",
                    },
                    jsonOptions,
                    statusCode: 409);
            }

            const string nextCodeSql =
                "SELECT ISNULL(CAST(MAX([code]) AS INT), 0) + 1 FROM dbo.substation WITH (UPDLOCK, HOLDLOCK);";
            int nextCode;
            await using (var cmdNext = new SqlCommand(nextCodeSql, conn, tx))
            {
                var nextObj = await cmdNext.ExecuteScalarAsync();
                nextCode = nextObj == null || nextObj == DBNull.Value ? 1 : Convert.ToInt32(nextObj);
            }

            var dropRoot = (config["Dropbox:RootNamespacePath"] ?? config["Dropbox:RootPath"] ?? "/SigmaUploads")
                .Trim()
                .TrimEnd('/');
            var beforePath = $"{dropRoot}/{nextCode}/Befor/";
            var afterPath = $"{dropRoot}/{nextCode}/After/";

            var cols = new List<string>();
            var vals = new List<string>();
            var prms = new List<SqlParameter>();
            foreach (var name in SubstationSave.ColumnNames)
            {
                if (name == "code")
                {
                    cols.Add("[code]");
                    vals.Add("@code");
                    prms.Add(new SqlParameter("@code", SqlDbType.Int) { Value = nextCode });
                    continue;
                }

                if (name == "before_path")
                {
                    cols.Add("[before_path]");
                    vals.Add("@before_path");
                    prms.Add(new SqlParameter("@before_path", SqlDbType.NVarChar, -1) { Value = beforePath });
                    continue;
                }

                if (name == "after_path")
                {
                    cols.Add("[after_path]");
                    vals.Add("@after_path");
                    prms.Add(new SqlParameter("@after_path", SqlDbType.NVarChar, -1) { Value = afterPath });
                    continue;
                }

                if (!root.TryGetProperty(name, out var el))
                {
                    continue;
                }

                cols.Add("[" + name + "]");
                vals.Add("@" + name);
                prms.Add(SubstationSave.MakeParameter(name, el));
            }

            if (cols.Count == 0)
            {
                await tx.RollbackAsync();
                return Results.BadRequest(new { error = "لا توجد حقول للإدراج." });
            }

            var insertSql = "INSERT INTO substation (" + string.Join(",", cols) + ") VALUES (" + string.Join(",", vals) + ")";

            await using (var cmd = new SqlCommand(insertSql, conn, tx))
            {
                foreach (var p in prms)
                {
                    cmd.Parameters.Add(p);
                }

                await cmd.ExecuteNonQueryAsync();
            }

            if (root.TryGetProperty("details", out var detailsEl) && detailsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in detailsEl.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        await tx.RollbackAsync();
                        return Results.BadRequest(new { error = "كل عنصر في details يجب أن يكون كائناً." });
                    }

                    var dCols = new List<string>();
                    var dVals = new List<string>();
                    var dPrms = new List<SqlParameter>();
                    foreach (var name in SubstationDetSave.ColumnNames)
                    {
                        if (name == "code")
                        {
                            dCols.Add("[code]");
                            dVals.Add("@d_code");
                            dPrms.Add(new SqlParameter("@d_code", SqlDbType.Int) { Value = nextCode });
                            continue;
                        }

                        if (!row.TryGetProperty(name, out var el))
                        {
                            continue;
                        }

                        dCols.Add("[" + name + "]");
                        dVals.Add("@" + name);
                        dPrms.Add(SubstationSave.MakeParameter(name, el));
                    }

                    if (dCols.Count == 0)
                    {
                        await tx.RollbackAsync();
                        return Results.BadRequest(new { error = "صف تفصيلي بدون حقول معروفة في substationdet." });
                    }

                    var detSql = "INSERT INTO substationdet (" + string.Join(",", dCols) + ") VALUES (" + string.Join(",", dVals) + ")";
                    await using (var detCmd = new SqlCommand(detSql, conn, tx))
                    {
                        foreach (var p in dPrms)
                        {
                            detCmd.Parameters.Add(p);
                        }

                        await detCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            await tx.CommitAsync();
            return Results.Json(new { ok = true, code = nextCode }, jsonOptions);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل إدراج substation / substationdet");
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
}

app.MapGet("/", (IConfiguration config) => Results.Json(new
{
    service = "Sigma.Api",
    database = config["ConnectionStrings:DatabaseNameHint"] ?? "SigmaDB",
}));

app.MapGet("/api/sigma/health", async (IConfiguration config, ILoggerFactory logs) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط في appsettings.");
    }

    try
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_NAME();", conn);
        var dbName = (string?)(await cmd.ExecuteScalarAsync());
        return Results.Json(new { ok = true, database = dbName });
    }
    catch (Exception ex)
    {
        var log = logs.CreateLogger("Sigma.Api");
        log.LogError(ex, "فشل الاتصال بقاعدة البيانات");
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 503);
    }
});

app.MapGet("/api/sigma/maincontractors", async (IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:MainContractorsSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:MainContractorsSql غير مضبوط.");
    }

    try
    {
        var rows = await ReadDynamicRowsAsync(cs, sql);
        return Results.Json(rows, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب قائمة maincontractor");
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/maincontractor/{code:int}", async (int code, IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:MainContractorByCodeSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:MainContractorByCodeSql غير مضبوط.");
    }

    try
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@code", code);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        if (!await reader.ReadAsync())
        {
            return Results.NotFound();
        }

        var row = ReadCurrentRowAsDictionary(reader);
        return Results.Json(row, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب مقاول code={Code}", code);
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/maincontractordet/list", async (int mainCode, IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:MainContractorDetListSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (mainCode <= 0)
    {
        return Results.BadRequest(new { error = "mainCode يجب أن يكون أكبر من صفر." });
    }

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:MainContractorDetListSql غير مضبوط.");
    }

    try
    {
        var rows = await ReadDynamicRowsAsync(cs, sql, [new SqlParameter("@code", mainCode)]);
        return Results.Json(rows, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب maincontractordet_view للمقاول {MainCode}", mainCode);
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/maincontractordet/detail", async (int mainCode, string? sCode, IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:MainContractorDetDetailSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (mainCode <= 0 || string.IsNullOrWhiteSpace(sCode))
    {
        return Results.BadRequest(new { error = "mainCode و sCode مطلوبان." });
    }

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:MainContractorDetDetailSql غير مضبوط.");
    }

    try
    {
        var row = await ReadSingleDynamicRowAsync(cs, sql,
        [
            new SqlParameter("@code", mainCode),
            new SqlParameter("@sCode", sCode),
        ]);

        return row is null ? Results.NotFound() : Results.Json(row, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب صف قطاع mainCode={MainCode} sCode={SCode}", mainCode, sCode);
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/employees", async (IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:EmployeesListSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:EmployeesListSql غير مضبوط.");
    }

    try
    {
        var rows = await ReadDynamicRowsAsync(cs, sql);
        return Results.Json(rows, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب قائمة employee");
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/typeworks", async (IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:TypeWorksSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:TypeWorksSql غير مضبوط.");
    }

    try
    {
        var rows = await ReadDynamicRowsAsync(cs, sql);
        return Results.Json(rows, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب قائمة typework");
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/bands", async (IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:BandsSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:BandsSql غير مضبوط.");
    }

    try
    {
        var rows = await ReadDynamicRowsAsync(cs, sql);
        return Results.Json(rows, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب قائمة band");
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/substation/next-code", async (IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:SubstationNextCodeSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:SubstationNextCodeSql غير مضبوط.");
    }

    try
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var scalar = await cmd.ExecuteScalarAsync();
        var nextCode = 1;
        if (scalar != null && scalar != DBNull.Value)
        {
            nextCode = Convert.ToInt32(scalar);
        }
        return Results.Json(new { nextCode }, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب كود المحطة التالي من substation");
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/typework-super/{sCode}", async (string sCode, IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:TypeworkSuperBySectorSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(sCode))
    {
        return Results.BadRequest(new { error = "sCode مطلوب." });
    }

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:TypeworkSuperBySectorSql غير مضبوط.");
    }

    try
    {
        var row = await ReadSingleDynamicRowAsync(cs, sql, [new SqlParameter("@sCode", sCode)]);
        return row is null ? Results.NotFound() : Results.Json(row, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب بيانات typework_super_view s_code={SCode}", sCode);
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/comdata/default", async (IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:DefaultComdataSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:DefaultComdataSql غير مضبوط.");
    }

    try
    {
        var row = await ReadSingleDynamicRowAsync(cs, sql);
        return row is null ? Results.NotFound() : Results.Json(row, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب بيانات comdata_view الافتراضية");
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapGet("/api/sigma/employee/{code:int}", async (int code, IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:EmployeeByCodeSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (code <= 0)
    {
        return Results.BadRequest(new { error = "code يجب أن يكون أكبر من صفر." });
    }

    if (string.IsNullOrWhiteSpace(cs))
    {
        return Results.Problem("ConnectionStrings:DefaultConnection غير مضبوط.");
    }

    if (string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("SigmaQueries:EmployeeByCodeSql غير مضبوط.");
    }

    try
    {
        var row = await ReadSingleDynamicRowAsync(cs, sql, [new SqlParameter("@code", code)]);
        return row is null ? Results.NotFound() : Results.Json(row, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل جلب employee code={Code}", code);
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.MapPost("/api/sigma/auth/check-permission", async (PermissionCheckRequest req, IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:EmployeePermissionSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (req.Code <= 0 || string.IsNullOrWhiteSpace(req.SCode))
    {
        return Results.BadRequest(new { ok = false, error = "code و sCode مطلوبان." });
    }

    if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("إعدادات الاتصال أو الاستعلام غير مضبوطة.");
    }

    try
    {
        var exists = await ExistsAsync(
            cs,
            sql,
            [
                new SqlParameter("@code", req.Code),
                new SqlParameter("@sCode", req.SCode),
            ]);
        return Results.Json(new { ok = exists });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل فحص صلاحية المستخدم code={Code} s_code={SCode}", req.Code, req.SCode);
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 503);
    }
});

app.MapPost("/api/sigma/auth/check-password", async (PasswordCheckRequest req, IConfiguration config, ILoggerFactory lf) =>
{
    var cs = config.GetConnectionString("DefaultConnection");
    var sql = config["SigmaQueries:EmployeePasswordSql"];
    var log = lf.CreateLogger("Sigma.Api");

    if (req.Code <= 0 || string.IsNullOrWhiteSpace(req.Password))
    {
        return Results.BadRequest(new { ok = false, error = "code و password مطلوبان." });
    }

    if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(sql))
    {
        return Results.Problem("إعدادات الاتصال أو الاستعلام غير مضبوطة.");
    }

    try
    {
        var exists = await ExistsAsync(
            cs,
            sql,
            [
                new SqlParameter("@code", req.Code),
                new SqlParameter("@pass", req.Password),
            ]);
        return Results.Json(new { ok = exists });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل فحص كلمة المرور code={Code}", req.Code);
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 503);
    }
});

app.MapPost("/api/sigma/substation", PostSubstationSaveAsync);
app.MapPost("/api/sigma/save-substation", PostSubstationSaveAsync);

app.MapPost("/api/sigma/dropbox/upload", async (
    HttpRequest request,
    IConfiguration config,
    IHttpClientFactory httpFactory,
    ILoggerFactory lf,
    CancellationToken cancellationToken) =>
{
    var log = lf.CreateLogger("Sigma.Api");

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "multipart/form-data مطلوب." });
    }

    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var codeStr = form["stationCode"].ToString();
        if (!int.TryParse(codeStr, out var stationCode) || stationCode <= 0)
        {
            return Results.BadRequest(new { error = "stationCode غير صالح." });
        }

        var beforeRaw = form["before"].ToString();
        var isBefore = beforeRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
                       || beforeRaw == "1";

        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "حقل file مطلوب." });
        }

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        var (ok, message) = await DropboxStorage.UploadStationImageAsync(
            config,
            http,
            stationCode,
            isBefore,
            file.FileName,
            bytes,
            cancellationToken);

        if (!ok)
        {
            log.LogWarning("Dropbox: {Message}", message);
            return Results.Json(new { ok = false, error = message }, jsonOptions, statusCode: 502);
        }

        return Results.Json(new { ok = true, path = message }, jsonOptions);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "فشل رفع Dropbox");
        return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 503);
    }
});

app.Run();

static Dictionary<string, object?> ReadCurrentRowAsDictionary(SqlDataReader reader)
{
    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < reader.FieldCount; i++)
    {
        var name = reader.GetName(i);
        row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
    }

    return row;
}

static async Task<List<Dictionary<string, object?>>> ReadDynamicRowsAsync(
    string connectionString,
    string sql,
    IReadOnlyList<SqlParameter>? parameters = null)
{
    var list = new List<Dictionary<string, object?>>();

    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(sql, conn)
    {
        CommandType = CommandType.Text,
    };

    if (parameters is { Count: > 0 })
    {
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }
    }

    await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

    while (await reader.ReadAsync())
    {
        list.Add(ReadCurrentRowAsDictionary(reader));
    }

    return list;
}

static async Task<Dictionary<string, object?>?> ReadSingleDynamicRowAsync(
    string connectionString,
    string sql,
    IReadOnlyList<SqlParameter>? parameters = null)
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(sql, conn)
    {
        CommandType = CommandType.Text,
    };

    if (parameters is { Count: > 0 })
    {
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }
    }

    await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
    if (!await reader.ReadAsync())
    {
        return null;
    }

    return ReadCurrentRowAsDictionary(reader);
}

static async Task<bool> ExistsAsync(
    string connectionString,
    string sql,
    IReadOnlyList<SqlParameter>? parameters = null)
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(sql, conn)
    {
        CommandType = CommandType.Text,
    };

    if (parameters is { Count: > 0 })
    {
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }
    }

    var result = await cmd.ExecuteScalarAsync();
    return result != null && result != DBNull.Value;
}

/// <summary>فحص وجود صف داخل معاملة (مثلاً قبل إدراج محطة).</summary>
static async Task<bool> ExistsScalarAsync(
    SqlConnection conn,
    SqlTransaction tx,
    string sql,
    IReadOnlyList<SqlParameter> parameters)
{
    await using var cmd = new SqlCommand(sql, conn, tx)
    {
        CommandType = CommandType.Text,
    };

    foreach (var p in parameters)
    {
        cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value ?? DBNull.Value));
    }

    var result = await cmd.ExecuteScalarAsync();
    return result != null && result != DBNull.Value;
}

internal sealed record PermissionCheckRequest(int Code, string SCode);
internal sealed record PasswordCheckRequest(int Code, string Password);

