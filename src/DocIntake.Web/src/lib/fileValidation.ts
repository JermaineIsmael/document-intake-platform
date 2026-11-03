export const allowedExtensions = ['.pdf', '.docx'] as const

const signatures: Record<(typeof allowedExtensions)[number], number[]> = {
  '.pdf': [0x25, 0x50, 0x44, 0x46, 0x2d], // %PDF-
  '.docx': [0x50, 0x4b, 0x03, 0x04], // ZIP local file header
}

function extensionOf(fileName: string): string {
  const index = fileName.lastIndexOf('.')
  return index < 0 ? '' : fileName.slice(index).toLowerCase()
}

async function readHeader(file: Blob, length: number): Promise<Uint8Array> {
  const slice = file.slice(0, length)
  if (typeof slice.arrayBuffer === 'function') return new Uint8Array(await slice.arrayBuffer())
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(new Uint8Array(reader.result as ArrayBuffer))
    reader.onerror = () => reject(reader.error)
    reader.readAsArrayBuffer(slice)
  })
}

// Mirrors the API's FileValidator so obvious mistakes are reported before uploading.
// The API remains the authority and re-validates every upload.
export async function validateFile(file: File, maxUploadMb: number): Promise<string | null> {
  const extension = extensionOf(file.name)
  if (!(allowedExtensions as readonly string[]).includes(extension)) {
    return 'Only PDF (.pdf) and Word (.docx) files are supported.'
  }
  if (file.size === 0) {
    return 'The file is empty.'
  }
  if (file.size > maxUploadMb * 1024 * 1024) {
    return `The file is larger than the ${maxUploadMb} MB limit.`
  }

  const signature = signatures[extension as keyof typeof signatures]
  const header = await readHeader(file, signature.length)
  if (header.length !== signature.length || signature.some((byte, i) => header[i] !== byte)) {
    return `The file content does not look like a valid ${extension === '.pdf' ? 'PDF' : 'DOCX'} file.`
  }
  return null
}

export function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
