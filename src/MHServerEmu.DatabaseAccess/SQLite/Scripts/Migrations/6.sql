CREATE TABLE "PortalPasswordChangeOperation" (
	"AccountId"	INTEGER NOT NULL,
	"OperationId"	TEXT NOT NULL,
	"Outcome"	TEXT NOT NULL CHECK ("Outcome" IN ('succeeded', 'rejected', 'cancelled')),
	"CreatedAt"	TEXT NOT NULL,
	PRIMARY KEY("AccountId", "OperationId"),
	UNIQUE("OperationId")
);
