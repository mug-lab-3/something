namespace Something.Storage;

internal static class SharedHistoryViews
{
    // Facts of a generation are its explicit membership joined to immutable states. Views expose
    // the external scan UUID so typed queries are unchanged; tables use integer keys.
    // No view reconstructs membership from listings or folders.
    internal const string Sql = """
        CREATE VIEW observation AS
        SELECT g.Id ScanId,s.* EXCLUDE(Version) FROM generation_observation m JOIN scan g ON g.Number=m.Generation
        JOIN state_observation s ON s.Version=m.Version AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW file_name AS
        SELECT g.Id ScanId,s.* EXCLUDE(Version) FROM generation_observation m JOIN scan g ON g.Number=m.Generation
        JOIN state_file_name s ON s.Version=m.Version AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW stream AS
        SELECT g.Id ScanId,s.* EXCLUDE(Version) FROM generation_observation m JOIN scan g ON g.Number=m.Generation
        JOIN state_stream s ON s.Version=m.Version AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW folder_sample AS
        SELECT g.Id ScanId,s.* EXCLUDE(Version) FROM scan_folder_ref m JOIN scan g ON g.Number=m.Generation
        JOIN state_folder_sample s ON s.Version=m.SampleVersion AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW folder_name_history AS
        SELECT g.Id ScanId,s.* EXCLUDE(Version) FROM scan_folder_ref m JOIN scan g ON g.Number=m.Generation
        JOIN state_file_name s ON s.Version=m.ObservationVersion AND s.RecordIndex=m.RecordIndex;
        CREATE VIEW browser AS
        SELECT g.Id ScanId,m.ParentIndex,m.ParentSequence,e.RecordIndex,e.Ordinal,e.Name,
            e.Sequence,e.IsDirectory,e.Resolution,e.LinksComplete,e.StandardFileAttributes,
            NULL::UHUGEINT EntrySubtreeLogical,NULL::UHUGEINT EntrySubtreeAllocated,NULL::USMALLINT FolderSequence,
            NULL::BIGINT EntrySubtreeUnresolved,NULL::BIGINT UnobservedDirectories,e.StreamLogical,e.StreamAllocated
        FROM scan_listing_ref m JOIN scan g ON g.Number=m.Generation JOIN shared_entry e ON e.Listing=m.Listing WHERE NOT e.IsDirectory
        UNION ALL
        SELECT g.Id ScanId,m.ParentIndex,m.ParentSequence,e.RecordIndex,e.Ordinal,e.Name,
            e.Sequence,e.IsDirectory,e.Resolution,e.LinksComplete,e.StandardFileAttributes,f.EntrySubtreeLogical,f.EntrySubtreeAllocated,
            f.Sequence FolderSequence,f.EntrySubtreeUnresolved,f.UnobservedDirectories,e.StreamLogical,e.StreamAllocated
        FROM scan_listing_ref m JOIN scan g ON g.Number=m.Generation JOIN shared_entry e ON e.Listing=m.Listing
        LEFT JOIN scan_folder_ref r ON r.Generation=m.Generation AND r.RecordIndex=e.RecordIndex
        LEFT JOIN state_folder_sample f ON f.Version=r.SampleVersion AND f.RecordIndex=r.RecordIndex
        WHERE e.IsDirectory;
        """;

    internal const string DropSql =
        "DROP VIEW browser; DROP VIEW folder_name_history; DROP VIEW folder_sample; DROP VIEW stream; DROP VIEW file_name; DROP VIEW observation;";
}
