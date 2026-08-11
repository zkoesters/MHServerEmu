-- Add opaque portal account identifiers without exposing Account.Id through PortalBridge.
ALTER TABLE Account ADD COLUMN PortalAccountId TEXT;
UPDATE Account SET PortalAccountId = 'acct_' || lower(hex(randomblob(16))) WHERE PortalAccountId IS NULL;
CREATE UNIQUE INDEX "IX_Account_PortalAccountId" ON "Account" ("PortalAccountId");
