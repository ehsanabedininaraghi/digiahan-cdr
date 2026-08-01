/*
Rollback is deliberately manual so the wrong dated backup is not restored by accident.
1) Stop the receiver service.
2) Find the backup tables created by 00_BACKUP_BEFORE_V2_2.sql:
   SELECT name FROM sys.tables WHERE name LIKE 'DidarContacts_Backup_%' OR name LIKE 'DidarContactPhones_Backup_%';
3) In a transaction, delete current rows and insert from the selected backup tables.
4) Start the service and run 04_VERIFY_V2_2.sql.
*/
