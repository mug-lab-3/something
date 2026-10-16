namespace Something.Storage;

internal static class SharedHistoryViews
{
    internal const string Sql = """
        CREATE VIEW observation_ref AS
        SELECT DISTINCT ScanId,RecordIndex,VersionId FROM (
            SELECT m.ScanId,e.RecordIndex,e.ObservationVersion VersionId
            FROM scan_listing_ref m JOIN shared_entry e ON e.ScanId=m.ListingId
            UNION ALL SELECT ScanId,RecordIndex,ObservationVersion FROM scan_folder_ref WHERE ObservationVersion IS NOT NULL
            UNION ALL SELECT * FROM auxiliary_ref);
        CREATE VIEW observation AS
        SELECT m.ScanId,s.* EXCLUDE(ScanId) FROM observation_ref m JOIN state_observation s ON s.ScanId=m.VersionId AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW file_name AS
        SELECT m.ScanId,s.* EXCLUDE(ScanId) FROM observation_ref m JOIN state_file_name s ON s.ScanId=m.VersionId AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW stream AS
        SELECT m.ScanId,s.* EXCLUDE(ScanId) FROM observation_ref m JOIN state_stream s ON s.ScanId=m.VersionId AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW folder_sample AS
        SELECT m.ScanId,s.* EXCLUDE(ScanId) FROM scan_folder_ref m JOIN state_folder_sample s ON s.ScanId=m.SampleVersion AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW folder_name_history AS
        SELECT m.ScanId,s.* EXCLUDE(ScanId) FROM scan_folder_ref m JOIN state_file_name s ON s.ScanId=m.ObservationVersion AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW browser AS
        SELECT m.ScanId,m.ParentIndex,m.ParentSequence,e.RecordIndex,e.Ordinal,e.Name,
            e.Sequence,e.IsDirectory,e.Resolution,e.LinksComplete,e.StandardFileAttributes,
            NULL::UHUGEINT EntrySubtreeLogical,NULL::UHUGEINT EntrySubtreeAllocated,NULL::USMALLINT FolderSequence,
            NULL::BIGINT EntrySubtreeUnresolved,NULL::BIGINT UnobservedDirectories,e.StreamLogical,e.StreamAllocated
        FROM scan_listing_ref m JOIN shared_entry e ON e.ScanId=m.ListingId WHERE NOT e.IsDirectory
        UNION ALL
        SELECT m.ScanId,m.ParentIndex,m.ParentSequence,e.RecordIndex,e.Ordinal,e.Name,
            e.Sequence,e.IsDirectory,e.Resolution,e.LinksComplete,e.StandardFileAttributes,f.EntrySubtreeLogical,f.EntrySubtreeAllocated,
            f.Sequence FolderSequence,f.EntrySubtreeUnresolved,f.UnobservedDirectories,e.StreamLogical,e.StreamAllocated
        FROM scan_listing_ref m JOIN shared_entry e ON e.ScanId=m.ListingId
        LEFT JOIN scan_folder_ref r ON r.ScanId=m.ScanId AND r.RecordIndex=e.RecordIndex
        LEFT JOIN state_folder_sample f ON f.ScanId=r.SampleVersion AND f.RecordIndex=r.RecordIndex
        WHERE e.IsDirectory;
        """;
}
