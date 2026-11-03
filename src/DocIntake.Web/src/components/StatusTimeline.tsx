import { Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { CheckmarkCircleFilled, CircleRegular, DismissCircleFilled } from '@fluentui/react-icons'
import type { ProcessingStatus } from '../api/types'
import { timelineState, timelineSteps, type StepState } from '../lib/documentStatus'

const useStyles = makeStyles({
  list: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    listStyleType: 'none',
    padding: 0,
    margin: 0,
  },
  step: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalXS,
    paddingTop: tokens.spacingVerticalS,
    borderTop: `3px solid ${tokens.colorNeutralStroke2}`,
  },
  done: { borderTopColor: tokens.colorPaletteGreenBorderActive },
  current: { borderTopColor: tokens.colorBrandStroke1 },
  error: { borderTopColor: tokens.colorPaletteRedBorderActive },
  iconDone: { color: tokens.colorPaletteGreenForeground1, fontSize: '20px' },
  iconError: { color: tokens.colorPaletteRedForeground1, fontSize: '20px' },
  iconPending: { color: tokens.colorNeutralForeground4, fontSize: '20px' },
})

const stateText: Record<StepState, string> = {
  done: 'completed',
  current: 'in progress',
  pending: 'not started',
  error: 'stopped',
}

export function StatusTimeline({ processingStatus }: { processingStatus: ProcessingStatus }) {
  const styles = useStyles()
  const states = timelineState(processingStatus)

  return (
    <ol className={styles.list} aria-label="Processing progress">
      {timelineSteps.map((step, i) => {
        const state = states[i]
        const className = [styles.step, state !== 'pending' ? styles[state] : ''].join(' ')
        return (
          <li key={step.status} className={className} aria-label={`${step.label}: ${stateText[state]}`}>
            {state === 'done' && <CheckmarkCircleFilled className={styles.iconDone} aria-hidden />}
            {state === 'current' && <Spinner size="extra-tiny" aria-hidden />}
            {state === 'error' && <DismissCircleFilled className={styles.iconError} aria-hidden />}
            {state === 'pending' && <CircleRegular className={styles.iconPending} aria-hidden />}
            <Text size={200}>{step.label}</Text>
          </li>
        )
      })}
    </ol>
  )
}
