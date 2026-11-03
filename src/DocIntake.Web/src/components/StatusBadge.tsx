import { Badge } from '@fluentui/react-components'
import type { DocumentStatus } from '../api/types'
import { statusColors, statusLabels } from '../lib/documentStatus'

export function StatusBadge({ status }: { status: DocumentStatus }) {
  return (
    <Badge appearance="filled" color={statusColors[status]} size="large">
      {statusLabels[status]}
    </Badge>
  )
}
