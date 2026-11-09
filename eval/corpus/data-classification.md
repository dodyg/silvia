# Data classification

## Levels

Information has four levels: Public, Internal, Confidential and Restricted.
Restricted data includes payroll records and customer credentials, and it must
never leave the European Union.

## Encryption and retention

Confidential and Restricted data is encrypted at rest with AES-256. Backups are
kept for 35 days and then destroyed, and restores are tested every quarter.

## Labels

Documents carry their classification in the header. A document without a label
is treated as Confidential until its owner says otherwise.
