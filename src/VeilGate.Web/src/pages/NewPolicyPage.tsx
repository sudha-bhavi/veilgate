import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import Button from "../components/Button";
import TextField from "../components/TextField";
import { describeApiError } from "../lib/apiErrorText";
import { createPolicy, POLICY_TEMPLATES } from "../lib/policiesApi";
import { showToast } from "../lib/toast";

export default function NewPolicyPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [template, setTemplate] = useState<string>(POLICY_TEMPLATES[0].value);
  const [description, setDescription] = useState("");

  const create = useMutation({
    mutationFn: createPolicy,
    onSuccess: ({ id }) => {
      showToast("Policy created.", "success");
      void queryClient.invalidateQueries({ queryKey: ["policies"] });
      navigate(`/policies/${id}`);
    },
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    if (name.trim())
      create.mutate({
        name: name.trim(),
        template,
        description: description.trim(),
      });
  }

  return (
    <main className="mx-auto max-w-2xl space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold">New policy</h1>
        <p className="mt-1 text-muted">
          Pick a starting point. You can change everything in the editor before
          it goes for approval.
        </p>
      </header>

      <form
        onSubmit={submit}
        className="space-y-5 rounded-xl border border-line bg-card p-4"
      >
        <TextField
          label="Name"
          value={name}
          maxLength={80}
          onChange={(event) => setName(event.target.value)}
        />

        <fieldset>
          <legend className="mb-2 text-muted">Template</legend>
          <div className="space-y-2">
            {POLICY_TEMPLATES.map((option) => (
              <div
                key={option.value}
                className="rounded-lg border border-line p-3"
              >
                <label className="flex items-center gap-2 font-medium">
                  <input
                    type="radio"
                    name="template"
                    value={option.value}
                    checked={template === option.value}
                    onChange={() => setTemplate(option.value)}
                  />
                  <span>{option.label}</span>
                </label>
                <p className="mt-1 pl-6 text-muted">{option.help}</p>
              </div>
            ))}
          </div>
        </fieldset>

        <div>
          <label htmlFor="pol-description" className="mb-1 block text-muted">
            Description (optional)
          </label>
          <textarea
            id="pol-description"
            rows={3}
            maxLength={500}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
            className="w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent"
          />
        </div>

        {create.isError && (
          <p role="alert" className="text-danger">
            {describeApiError(create.error)}
          </p>
        )}

        <div className="flex gap-3">
          <Button
            type="submit"
            loading={create.isPending}
            disabled={!name.trim()}
          >
            Create policy
          </Button>
          <Link
            to="/policies"
            className="inline-flex items-center rounded-lg px-4 py-2 text-muted hover:text-fg"
          >
            Cancel
          </Link>
        </div>
      </form>
    </main>
  );
}
