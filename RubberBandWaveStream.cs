using System;
using System.Collections.Generic;
using System.Linq;

using NAudio.Wave;

namespace RubberBand.NAudio
{
	public class RubberBandWaveStream : IWaveProvider
	{
		IWaveProvider _source;
		byte[] _sourceBuffer;
		float[][] _sourceSamples;
		RubberBandStretcher _stretcher;
		float[][] _stretchedSamples;
		double _tempo;
		bool _tempoChanged;

		public RubberBandWaveStream(IWaveProvider source)
		{
			if (source.WaveFormat.BitsPerSample != 16)
				throw new FormatException("Can't process bit depth of " + source.WaveFormat.BitsPerSample);

			_source = source;

			_sourceSamples = Enumerable.Range(1, source.WaveFormat.Channels).Select(channel => new float[16384]).ToArray();
			_sourceBuffer = new byte[_sourceSamples.Sum(channel => channel.Length) * 2];
			_stretchedSamples = Enumerable.Range(1, source.WaveFormat.Channels).Select(channel => new float[16384]).ToArray();

			_stretcher = new RubberBandStretcher(_source.WaveFormat.SampleRate, _source.WaveFormat.Channels, RubberBandStretcher.Options.ProcessRealTime);

			_tempo = 1.0;
		}

		public double Tempo
		{
			get { return _tempo; }
			set
			{
				_tempo = value;
				_tempoChanged = true;
			}
		}

		public WaveFormat WaveFormat => _source.WaveFormat;

		List<byte> _sourceExtraBytes = new List<byte>();
		List<byte> _outputExtraBytes = new List<byte>();

		public event EventHandler SourceRead;
		public event EventHandler EndOfStream;

		public int Read(byte[] buffer, int offset, int count)
		{
			int numRead = 0;

			// Mismatched formats/interpretations:
			//
			// - Source returns raw bytes, lets us interpret.
			// - SoundTouch takes samples (Int16), counts one frame across all channels as a single sample (one left + one right == one sample).
			// - When converting to/from bytes, we need to count each channel in a frame as a separate sample (one left + one right == two samples).
			// - We implement IWaveProvider, the same as source, and are thus expected to return raw bytes.
			// - We may be asked for a number of bytes that isn't a multiple of the stretcher's output block size.
			// - We may be provided with source data that isn't a multiple of the stretcher's input block size.
			//
			// Hooray!

			if (_outputExtraBytes.Count > 0)
			{
				if (_outputExtraBytes.Count > count)
				{
					_outputExtraBytes.CopyTo(0, buffer, offset, count);
					_outputExtraBytes.RemoveRange(0, count);

					return count;
				}
				else
				{
					_outputExtraBytes.CopyTo(buffer);

					count -= _outputExtraBytes.Count;
					numRead += _outputExtraBytes.Count;

					_outputExtraBytes.Clear();
				}
			}

			int bytesPerFrame = 2 * _source.WaveFormat.Channels;

			while (true)
			{
				int stretchedFramesToRead = (count + bytesPerFrame - 1) / bytesPerFrame;

				if (stretchedFramesToRead > _stretchedSamples[0].Length)
					stretchedFramesToRead = _stretchedSamples[0].Length;

				if (_tempoChanged)
				{
					_stretcher.SetTimeRatio(1.0 / _tempo);
					_tempoChanged = false;
				}

				int numberOfFramesRead = (int)_stretcher.Retrieve(_stretchedSamples, stretchedFramesToRead);

				if (numberOfFramesRead == 0)
				{
					int sourceBytesRead = _sourceExtraBytes.Count;

					if (sourceBytesRead > 0)
					{
						_sourceExtraBytes.CopyTo(_sourceBuffer);
						_sourceExtraBytes.Clear();
					}

					sourceBytesRead += _source.Read(_sourceBuffer, sourceBytesRead, _sourceBuffer.Length - sourceBytesRead);

					SourceRead?.Invoke(this, EventArgs.Empty);

					if (sourceBytesRead == 0)
					{
						// End of stream, zero pad
						Array.Clear(buffer, offset, count);

						numRead += count;

						EndOfStream?.Invoke(this, EventArgs.Empty);

						return numRead;
					}

					int numberOfSourceSamplesPerChannel = sourceBytesRead / 2 / _source.WaveFormat.Channels;

					int sourceBytesInSamples = numberOfSourceSamplesPerChannel * _source.WaveFormat.Channels * 2;

					if (sourceBytesInSamples < sourceBytesRead)
					{
						// We got a misaligned read, stash the bytes we aren't going to process for the next pass.
						for (int i = sourceBytesInSamples; i < sourceBytesRead; i++)
							_sourceExtraBytes.Add(_sourceBuffer[i]);
					}

					for (int channel = 0; channel < _source.WaveFormat.Channels; channel++)
					{
						int channelOffset = channel * 2;

						for (int i = 0; i < numberOfSourceSamplesPerChannel; i++)
						{
							int lo = _sourceBuffer[i * bytesPerFrame + channelOffset];
							int hi = _sourceBuffer[i * bytesPerFrame + channelOffset + 1];

							short sampleValue = unchecked((short)((hi << 8) | lo));

							_sourceSamples[channel][i] = sampleValue * (1.0f / 32768.0f);
						}
					}

					_stretcher.Process(_sourceSamples, numberOfSourceSamplesPerChannel, final: false);
				}
				else
				{
					int i = 0;
					int channel = 0;

					while (i < numberOfFramesRead)
					{
						if (count == 0)
							break;

						float rawSample = _stretchedSamples[channel][i];

						channel++;

						if (channel == _source.WaveFormat.Channels)
						{
							channel = 0;
							i++;
						}

						unchecked
						{
							short sample;

							if (rawSample <= -1.0)
								sample = -32768;
							else if (rawSample >= 1.0)
								sample = +32767;
							else
							{
								int wideSample = (int)(rawSample * 32768.0f);

								if (wideSample < -32768)
									sample = -32768;
								else if (wideSample > 32767)
									sample = 32767;
								else
									sample = (short)wideSample;
							}

							byte hi = (byte)(sample >> 8);
							byte lo = (byte)(sample & 0xFF);

							buffer[offset++] = lo;
							numRead++;
							count--;

							if (count == 0)
							{
								_outputExtraBytes.Add(hi);
								break;
							}

							buffer[offset++] = hi;
							numRead++;
							count--;
						}
					}

					while (i < numberOfFramesRead)
					{
						float rawSample = _stretchedSamples[channel][i];

						channel++;

						if (channel == _source.WaveFormat.Channels)
						{
							channel = 0;
							i++;
						}

						unchecked
						{
							short sample;

							if (rawSample <= -1.0)
								sample = -32768;
							else if (rawSample >= 1.0)
								sample = +32767;
							else
							{
								int wideSample = (int)(rawSample * 32768.0f);

								if (wideSample < -32768)
									sample = -32768;
								else if (wideSample > 32767)
									sample = 32767;
								else
									sample = (short)wideSample;
							}

							byte hi = (byte)(sample >> 8);
							byte lo = (byte)(sample & 0xFF);

							_outputExtraBytes.Add(lo);
							_outputExtraBytes.Add(hi);
						}
					}

					if (count == 0)
						return numRead;
				}
			}
		}
	}
}
