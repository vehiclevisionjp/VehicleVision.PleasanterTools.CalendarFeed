-- 検証用の API キーを Administrator へ設定する。
--
-- Pleasanter は Users.ApiKey を **そのまま比較する**（Libraries/Requests/Context.cs の
-- SetUserProperties が Rds.UsersWhere().ApiKey(ApiKey) で突き合わせる）。
-- 実運用のキーは Guid.NewGuid().ToString().Sha512Cng()（UserModel.CreateApiKey）で
-- 生成された SHA-512 の 16 進 128 文字。ここでは検証の再現性のため固定値を入れる。
--
-- **この値は検証環境専用。** 本番の Pleasanter に対して実行しないこと。
SET NOCOUNT ON;
USE [Implem.Pleasanter];

DECLARE @ApiKey nvarchar(128) =
    N'6ce6c0fd2f4aea3c093c3ebdd7d4ea3250a132b4867d68e7d1abe35c2499664abb398d74603c2b2a38e31a21319955b3c43ede30394ead7be37ddd615c34e1f6';

UPDATE [Users] SET [ApiKey] = @ApiKey WHERE [LoginId] = N'Administrator';

IF @@ROWCOUNT = 0
BEGIN
    RAISERROR('Administrator が見つからない。CodeDefiner が失敗している可能性がある。', 16, 1);
END

-- AllowApi 列がある版では有効化する（無い版では何もしない）
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[Users]') AND name = N'AllowApi')
BEGIN
    EXEC sp_executesql N'UPDATE [Users] SET [AllowApi] = 1 WHERE [LoginId] = N''Administrator''';
END

SELECT [UserId], [LoginId], LEN([ApiKey]) AS ApiKeyLength FROM [Users] WHERE [LoginId] = N'Administrator';
