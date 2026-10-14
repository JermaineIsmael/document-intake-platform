-- Load Test Completion Verification
--
-- Run this query 60-90 seconds after the load test completes (load-test-upload.ps1)
-- to verify all uploaded documents are indexed in SQL and AI Search.
--
-- Replace the document IDs from the load test output.

-- Check SQL: All documents should be present with ChunkCount > 0
SELECT 
    COUNT(*) as TotalDocuments,
    SUM(CAST(ChunkCount AS BIGINT)) as TotalChunks,
    MAX(IndexedAt) as MostRecentIndexTime,
    MIN(IndexedAt) as OldestIndexTime,
    DATEDIFF(MINUTE, MIN(IndexedAt), MAX(IndexedAt)) as ProcessingSpanMinutes
FROM dbo.Documents
WHERE Id IN (
    -- PASTE document IDs here from load test output
    -- 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
    -- 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'
);

-- Expected: All documents indexed with realistic chunk counts
-- Typical result (for 20 5-page PDFs):
--   TotalDocuments: 20
--   TotalChunks: ~200-300 (10-15 chunks per document)
--   ProcessingSpanMinutes: 1-3

-- Check for missing documents
SELECT 
    Id,
    CASE WHEN ChunkCount = 0 THEN 'Not yet indexed' ELSE 'Indexed' END as Status,
    ChunkCount,
    DATEDIFF(SECOND, IndexedAt, GETDATE()) as SecondsSinceIndexed
FROM dbo.Documents
WHERE Id IN (
    -- PASTE document IDs here
)
ORDER BY IndexedAt DESC;

-- Check for dead-lettered documents (should be 0)
-- Note: Run from Service Bus Explorer or Azure CLI
-- az servicebus queue dead-letter show --resource-group <rg> --namespace-name <ns> --queue-name extracted-documents
-- Expected: No dead-letter messages

-- AI Search verification (requires Search token)
-- POST https://<search-service>.search.windows.net/indexes/document-chunks/docs/search?api-version=2024-07-01
-- Body: {
--   "search": "*",
--   "count": true,
--   "top": 0
-- }
-- Expected: "@odata.count" equals or exceeds total chunk count from SQL

