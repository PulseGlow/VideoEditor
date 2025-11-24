dependencies = ['torch', 'torchaudio']
import torch
import json
import os
from utils_vad import (init_jit_model,
                       get_speech_timestamps,
                       get_number_ts,
                       get_language,
                       get_language_and_group,
                       save_audio,
                       read_audio,
                       VADIterator,
                       collect_chunks,
                       drop_chunks,
                       Validator,
                       OnnxWrapper)


def silero_vad(onnx=True, force_onnx_cpu=True):
    """Silero Voice Activity Detector
    Returns a model with a set of utils
    Please see https://github.com/snakers4/silero-vad for usage examples
    """
    model_dir = os.path.join(os.path.dirname(__file__), 'files')
    if onnx:
        model = OnnxWrapper(os.path.join(model_dir, 'silero_vad.onnx'), force_onnx_cpu)
    else:
        model = init_jit_model(os.path.join(model_dir, 'silero_vad.jit'))
    utils = (get_speech_timestamps,
             save_audio,
             read_audio,
             VADIterator,
             collect_chunks)

    return model, utils

