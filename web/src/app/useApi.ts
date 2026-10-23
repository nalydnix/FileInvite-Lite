import { useCallback, useEffect, useState } from 'react'
import { ApiError, apiRequest } from './apiClient'
import { useAuth } from './authState'

/**
 * How a component talks to the API. Wraps apiClient so a page gets loading, error and refetch
 * state without calling `fetch` or `apiClient` itself, and so the bearer token and the 401
 * handling live here once rather than in every page.
 */

const UNREACHABLE = 'Could not reach the server. Check it is running and try again.'

/** Anything that is not a problem response — a dropped connection, say — reads as one. */
function toApiError(caught: unknown): ApiError {
  return caught instanceof ApiError ? caught : new ApiError(0, { title: UNREACHABLE })
}

export interface ApiQuery<T> {
  data: T | null
  error: ApiError | null
  loading: boolean
  refetch: () => void
}

/** GET `path` on mount and whenever `path` changes. Build the path with the query string in it. */
export function useApi<T>(path: string): ApiQuery<T> {
  const { token, signOut } = useAuth()
  const [state, setState] = useState<Omit<ApiQuery<T>, 'refetch'>>({
    data: null,
    error: null,
    loading: true,
  })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setState((previous) => ({ ...previous, error: null, loading: true }))

    apiRequest<T>(path, { token: token ?? undefined, signal: controller.signal })
      .then((data) => setState({ data, error: null, loading: false }))
      .catch((caught: unknown) => {
        if (controller.signal.aborted) return
        const error = toApiError(caught)
        // An expired or rejected token ends the session; RequireStaffAuth sends the user to login.
        if (error.status === 401) signOut()
        setState((previous) => ({ ...previous, error, loading: false }))
      })

    return () => controller.abort()
  }, [path, token, signOut, attempt])

  const refetch = useCallback(() => setAttempt((count) => count + 1), [])

  return { ...state, refetch }
}

export interface ApiMutation<TBody, TResult> {
  /** Resolves to the response, or `null` when the call failed (the reason is in `error`). */
  run: (body: TBody) => Promise<TResult | null>
  error: ApiError | null
  loading: boolean
}

/** POST a body to `path` on demand. */
export function useApiMutation<TBody, TResult>(path: string): ApiMutation<TBody, TResult> {
  const { token, signOut } = useAuth()
  const [error, setError] = useState<ApiError | null>(null)
  const [loading, setLoading] = useState(false)

  const run = useCallback(
    async (body: TBody) => {
      setError(null)
      setLoading(true)
      try {
        return await apiRequest<TResult>(path, {
          method: 'POST',
          body,
          token: token ?? undefined,
        })
      } catch (caught) {
        const failure = toApiError(caught)
        if (failure.status === 401) signOut()
        setError(failure)
        return null
      } finally {
        setLoading(false)
      }
    },
    [path, token, signOut],
  )

  return { run, error, loading }
}
