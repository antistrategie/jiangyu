export function generatePatchKdl(templateType: string, templateId: string): string {
  return `patch "${templateType}" "${templateId}" {\n    // set "FieldName" value\n}\n`;
}

export function generateCloneKdl(templateType: string, sourceId: string, cloneId: string): string {
  return `clone "${templateType}" from="${sourceId}" id="${cloneId}" {\n    // set "FieldName" value\n}\n`;
}
