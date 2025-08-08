# Azure Blob Storage Upload Feature

The HtmlGenerator now supports uploading generated HTML content directly to Azure Blob Storage instead of writing to a local directory.

## Usage

### Command Line Syntax
```
HtmlGenerator.exe [existing-options] /azureblob:"container-name"
```

### Example
```
HtmlGenerator.exe /in:"C:\path\to\solution.sln" /azureblob:"mycontainer"
```

### When to Use
- Use `/azureblob:"container-name"` when you want to upload generated HTML directly to Azure Blob Storage
- Use without `/azureblob` for traditional local file output to the `/out` directory

## Setup Required

Before using the Azure Blob Storage feature, you need to:

1. **Initialize BlobServiceClient** in `Program.cs`:
   - Locate the TODO comment in `Program.cs` 
   - Replace it with proper Azure authentication setup
   - Example:
   ```csharp
   // TODO: Initialize BlobServiceClient here (e.g., using connection string, managed identity, etc.)
   OutputSystem = new AzureBlobOutputSystem(blobServiceClient, options.AzureBlobContainer);
   ```

2. **Azure Authentication Options**:
   - Connection string: `new BlobServiceClient(connectionString)`
   - Managed Identity: `new BlobServiceClient(new Uri(accountUri), new DefaultAzureCredential())`
   - Service Principal: Configure with appropriate credentials

## Architecture

The feature uses an abstraction layer with two implementations:

### IOutputSystem Interface
- `WriteAllTextAsync()` - Write text content to a file
- `WriteAllBytesAsync()` - Write binary content to a file  
- `CopyFileAsync()` - Copy files
- `CreateDirectoryAsync()` - Create directories (no-op for blob storage)
- `OpenWriteStreamAsync()` - Open a stream for writing

### Implementations
- **LocalFileOutputSystem**: Traditional file system operations
- **AzureBlobOutputSystem**: Azure Blob Storage operations

### OutputHelper Class
Provides both synchronous and asynchronous wrappers for file operations that automatically use the configured output system.

## File Path Handling

When using Azure Blob Storage:
- Local file paths are normalized to blob names
- Directory separators (`\`) are converted to forward slashes (`/`)
- Leading slashes are removed
- Example: `C:\output\index\file.html` becomes `index/file.html` in the blob container

## Compatibility

- Maintains full backward compatibility with existing command-line options
- Falls back to local file system when no Azure container is specified
- All existing functionality continues to work unchanged
