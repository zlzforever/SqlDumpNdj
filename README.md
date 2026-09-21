# Expert data exporter

这是一个 .NET 8 控制台程序，用于导出：

- `res_expert` -> `res_expert.ndjson.gz`
- `res_employment_history` -> `res_employment_history.ndjson.gz`

每张表一个文件，每行一个 JSON 对象。程序按 `id` 使用 keyset pagination 分批查询，不使用 `OFFSET`，并且只在内存中保留当前批次的数据库读取状态。`id` 不限定为 `long`：数字、字符串等数据库可排序类型都会使用数据库返回的原始类型作为下一批的游标。

每张表导出完成后，程序会执行 `SELECT COUNT(*)` 与实际写出的行数比较。数量不一致会输出 `ERROR` 到标准错误，并以退出码 `1` 结束；数量一致才会返回退出码 `0`。

## 使用

```bash
dotnet restore
dotnet run -- \
  --connection "Server=127.0.0.1;Port=3306;Database=your_db;User ID=your_user;Password=your_password;" \
  --table res_expert \
  --table res_employment_history \
  --output ./export \
  --batch-size 1000
```

`--table` 可以重复传入；如果不传，则默认导出 `res_expert` 和 `res_employment_history`。每个表生成一个 `<table-name>.ndjson.gz` 文件。

也可以使用环境变量：

```bash
export MYSQL_CONNECTION_STRING='Server=127.0.0.1;Database=your_db;User ID=your_user;Password=your_password;'
dotnet run -- --batch-size 2000
```

## 压缩说明

输出使用 GZip 的 `Optimal` 压缩级别，文件名以 `.gz` 结尾。解压示例：

```bash
gzip -dk export/res_expert.ndjson.gz
```

如果数据中有大量重复字段名和文本，压缩率通常会明显高于 CSV；代价是下游读取时需要支持 gzip 和 NDJSON。程序先写入 `.tmp` 文件，成功完成后再替换正式文件，失败时会清理临时文件。
