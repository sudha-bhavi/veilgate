import { useState, type ReactNode } from "react";
import { useMutation, type UseMutationResult } from "@tanstack/react-query";
import StepUpDialog from "../components/StepUpDialog";
import { needsStepUp } from "./stepUp";

type Options<TData, TVariables> = {
  mutationFn: (variables: TVariables) => Promise<TData>;
  onSuccess: (data: TData) => void;
  /** Any failure other than "confirm your identity", which this hook handles itself. */
  onFailure: (error: unknown) => void;
};

/**
 * For calls that may answer "confirm your identity" (publish, rollback): run the call; if the server asks for a fresh
 * check, show the dialog, and when the person confirms, run the same call again.
 */
export function useStepUpMutation<TData, TVariables = void>({
  mutationFn,
  onSuccess,
  onFailure,
}: Options<TData, TVariables>): {
  mutation: UseMutationResult<TData, Error, TVariables>;
  stepUpDialog: ReactNode;
} {
  const [waiting, setWaiting] = useState<{ variables: TVariables } | null>(
    null,
  );

  const mutation = useMutation<TData, Error, TVariables>({
    mutationFn,
    onSuccess,
    onError: (error, variables) => {
      if (needsStepUp(error)) setWaiting({ variables });
      else onFailure(error);
    },
  });

  const stepUpDialog = waiting ? (
    <StepUpDialog
      onCancel={() => setWaiting(null)}
      onConfirmed={() => {
        const { variables } = waiting;
        setWaiting(null);
        mutation.mutate(variables); // the session now carries a fresh token, so the same call goes through
      }}
    />
  ) : null;

  return { mutation, stepUpDialog };
}
